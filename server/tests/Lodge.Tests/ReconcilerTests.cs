using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Domain.Enums;
using Lodge.Core.Reconciliation;
using Xunit;

namespace Lodge.Tests;

public class ReconcilerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset T(int minutes) => T0.AddMinutes(minutes);

    private static ActionIdentity Id(string path, string? key, string actionKey) => new(path, key, actionKey);

    private static SucceededRecord Succeeded(
        ActionIdentity identity, SignalTrigger trigger, string? desiredValueJson, DateTimeOffset completedAt, bool synthetic)
        => new(Guid.NewGuid(), identity, trigger, desiredValueJson, completedAt, synthetic);

    private static ReconciliationInput Input(
        CapabilityCatalog catalog,
        string desiredYaml,
        IReadOnlyList<SucceededRecord>? history = null,
        IReadOnlyList<LiveActionRow>? live = null) => new(
            "acme", "instance-alpha",
            YamlFlattener.Parse(desiredYaml),
            catalog,
            history ?? Array.Empty<SucceededRecord>(),
            live ?? Array.Empty<LiveActionRow>());

    // --- Catalogs ----------------------------------------------------------------------

    private static CapabilityCatalog SsoCatalog(string redeployPolicy = "MANUAL_REQUIRED")
    {
        var capability = CapabilityCatalogLoader.LoadCapability($$"""
            capability: sso
            title: "Single Sign-On"
            signals:
              - path: features.sso_login
                rules:
                  - when: true
                    actions:
                      - key: configure_sso
                        runbook: acme/configure-sso
                        label: "Configure SSO"
                        policy: AUTO
                        inputs:
                          instance: { from: instance }
                          mode: { const: "enable" }
                      - key: redeploy
                        runbook: acme/redeploy
                        label: "Redeploy"
                        policy: {{redeployPolicy}}
                        inputs:
                          instance: { from: instance }
                  - when: false
                    actions:
                      - key: configure_sso
                        runbook: acme/configure-sso
                        label: "Disable SSO"
                        policy: AUTO
                        inputs:
                          mode: { const: "disable" }
                      - key: redeploy
                        runbook: acme/redeploy
                        label: "Redeploy"
                        policy: MANUAL_REQUIRED
            """);
        return CapabilityCatalogLoader.Merge("acme", new[] { capability }, Array.Empty<CapabilityDefinition>());
    }

    private static CapabilityCatalog VmCatalog(params CapabilityDefinition[] overrides)
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            title: "Virtual Machines"
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: provision_vm
                        runbook: acme/provision-vm
                        label: "Provision VM"
                        policy: MANUAL_REQUIRED
                        inputs:
                          vm_name: { from: key }
                          spec: { from: item }
                  - on: modify
                    actions:
                      - key: update_vm
                        runbook: acme/update-vm
                        label: "Update VM"
                        policy: MANUAL_REQUIRED
                        inputs:
                          vm_name: { from: key }
                          spec: { from: item }
                  - on: delete
                    actions:
                      - key: destroy_vm
                        runbook: acme/destroy-vm
                        label: "Destroy VM"
                        policy: MANUAL_REQUIRED
                        inputs:
                          vm_name: { from: key }
            """);
        return CapabilityCatalogLoader.Merge("acme", new[] { capability }, overrides);
    }

    private static CapabilityCatalog VmCatalogWithDependency()
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            title: "Virtual Machines"
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: provision_vm
                        runbook: acme/provision-vm
                        policy: MANUAL_REQUIRED
                        inputs:
                          vm_name: { from: key }
                  - on: modify
                    actions:
                      - key: update_vm
                        runbook: acme/update-vm
                        policy: MANUAL_REQUIRED
                        depends_on: [ "virtual_machines.provision_vm" ]
                        inputs:
                          vm_name: { from: key }
            """);
        return CapabilityCatalogLoader.Merge("acme", new[] { capability }, Array.Empty<CapabilityDefinition>());
    }

    private static CapabilityCatalog QuirksCatalog()
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: quirks
            title: "Quirks"
            signals:
              - path: quirks
                kind: scalar_list
                rules:
                  - on: add
                    actions:
                      - key: investigate_quirk
                        runbook: acme/investigate-quirk
                        label: "Investigate"
                        policy: OPTIONAL
                        inputs:
                          quirk: { from: key }
                  - on: delete
                    actions:
                      - key: log_quirk_resolved
                        runbook: acme/log-quirk-resolved
                        label: "Log resolved"
                        policy: AUTO
                        inputs:
                          quirk: { from: key }
            """);
        return CapabilityCatalogLoader.Merge("acme", new[] { capability }, Array.Empty<CapabilityDefinition>());
    }

    // --- past_history --------------------------------------------------------------------

    [Fact]
    public void Past_history_exact_address_adopts_just_that_one_action()
    {
        var result = Reconciler.Reconcile(Input(SsoCatalog(), """
            features:
              sso_login: true
            past_history:
              - date_utc: 2023-01-15
                action: features.sso_login.configure_sso
                description: "already configured before Lodge"
            """));

        var adopt = Assert.Single(result.ToAdopt);
        Assert.Equal(Id("features.sso_login", null, "configure_sso"), adopt.Identity);
        Assert.Equal("true", adopt.DesiredValueJson);
        // redeploy isn't covered by the exact address — it's real drift as usual.
        Assert.Contains(result.ToCreate, a => a.Identity.ActionKey == "redeploy");
    }

    [Fact]
    public void Past_history_exact_address_stops_adopting_once_materialized()
    {
        var yaml = """
            features:
              sso_login: true
            past_history:
              - date_utc: 2023-01-15
                action: features.sso_login.configure_sso
                description: "already configured before Lodge"
            """;

        var first = Reconciler.Reconcile(Input(SsoCatalog(), yaml));
        var adopted = Assert.Single(first.ToAdopt);

        var history = new[]
        {
            Succeeded(adopted.Identity, SignalTrigger.STATE, adopted.DesiredValueJson, T(1), true)
        };
        var second = Reconciler.Reconcile(Input(SsoCatalog(), yaml, history));

        // Once materialized the identity is no longer "never succeeded" — leaving the
        // entry in YAML forever is harmless for an exact address.
        Assert.Empty(second.ToAdopt);
    }

    [Fact]
    public void Past_history_item_wildcard_covers_every_action_key_of_that_item_including_a_later_modify()
    {
        var pastHistory = """
            past_history:
              - date_utc: 2023-01-15
                action: virtual_machines[vm1].*
                description: "legacy VM, all actions presumed already applied"
            """;

        var stepA = Reconciler.Reconcile(Input(VmCatalog(), $"virtual_machines:\n  vm1:\n    size: small\n{pastHistory}"));
        var adopted = Assert.Single(stepA.ToAdopt);
        Assert.Equal(Id("virtual_machines", "vm1", "provision_vm"), adopted.Identity);

        var history = new[]
        {
            Succeeded(adopted.Identity, SignalTrigger.ADD, adopted.DesiredValueJson, T(1), true)
        };

        // Confirmed by design: the item-scoped wildcard stays forever-open while it's in
        // YAML, so the *first* modify on this item is also adopted on faith rather than
        // surfacing as real drift — the operational discipline is a two-commit workflow
        // (commit the past_history entry with the change, let it materialize, then remove
        // the entry in a follow-up commit to restore normal drift detection).
        var stepB = Reconciler.Reconcile(Input(
            VmCatalog(), $"virtual_machines:\n  vm1:\n    size: large\n{pastHistory}", history));

        var modifyAdopted = Assert.Single(stepB.ToAdopt);
        Assert.Equal(Id("virtual_machines", "vm1", "update_vm"), modifyAdopted.Identity);
        Assert.Equal(SignalTrigger.MODIFY, modifyAdopted.Trigger);
        Assert.Empty(stepB.ToCreate);
    }

    [Fact]
    public void Past_history_universal_marker_keeps_covering_brand_new_items_that_appear_later()
    {
        var pastHistory = """
            past_history:
              - date_utc: 2023-07-15T11:00:00Z
                action: "*"
                description: "Initial import"
            """;

        var stepA = Reconciler.Reconcile(Input(VmCatalog(), $"virtual_machines:\n  vm1:\n    size: small\n{pastHistory}"));
        var adopted = Assert.Single(stepA.ToAdopt);
        Assert.Equal(Id("virtual_machines", "vm1", "provision_vm"), adopted.Identity);

        var history = new[]
        {
            Succeeded(adopted.Identity, SignalTrigger.ADD, adopted.DesiredValueJson, T(1), true)
        };

        // A brand-new item shows up later — the universal marker, unlike an exact
        // address, is deliberately forever-open and still adopts it.
        var stepB = Reconciler.Reconcile(Input(
            VmCatalog(), $"virtual_machines:\n  vm1:\n    size: small\n  vm2:\n    size: small\n{pastHistory}", history));

        var newAdopted = Assert.Single(stepB.ToAdopt);
        Assert.Equal(Id("virtual_machines", "vm2", "provision_vm"), newAdopted.Identity);
        Assert.Empty(stepB.ToCreate);
    }

    [Fact]
    public void Past_history_universal_marker_without_description_is_a_validation_error_and_not_adopted()
    {
        var result = Reconciler.Reconcile(Input(SsoCatalog(), """
            features:
              sso_login: true
            past_history:
              - date_utc: 2023-01-01
                action: "*"
            """));

        Assert.Contains(result.ValidationErrors, e => e.Contains("requires a non-empty 'description'"));
        Assert.Empty(result.ToAdopt);
        Assert.Equal(2, result.ToCreate.Count);
    }

    [Fact]
    public void Past_history_item_key_wildcard_is_rejected()
    {
        var result = Reconciler.Reconcile(Input(VmCatalog(), """
            virtual_machines:
              vm1:
                size: small
            past_history:
              - date_utc: 2023-01-01
                action: "virtual_machines[*].provision_vm"
                description: "oops"
            """));

        Assert.Contains(result.ValidationErrors, e => e.Contains("item-key wildcards are not supported"));
        Assert.Empty(result.ToAdopt);
    }

    [Fact]
    public void Without_past_history_the_same_state_is_real_drift()
    {
        var result = Reconciler.Reconcile(Input(SsoCatalog(), "features:\n  sso_login: true"));

        Assert.Empty(result.ToAdopt);
        Assert.Equal(2, result.ToCreate.Count);
        // AUTO drift runs itself; MANUAL waits for a human.
        var auto = Assert.Single(result.ToAutoStart);
        Assert.Equal("configure_sso", auto.Identity.ActionKey);
        Assert.Equal(CapabilityState.Pending, Assert.Single(result.Capabilities).State);
    }

    // --- depends_on ------------------------------------------------------------------------

    [Fact]
    public void Action_with_unmet_dependency_is_fully_absent_and_reappears_once_satisfied()
    {
        var catalog = VmCatalogWithDependency();
        var history = new[]
        {
            Succeeded(Id("virtual_machines", "vm1", "some_other_key"), SignalTrigger.ADD, "{\"size\":\"small\"}", T(1), false)
        };

        var blocked = Reconciler.Reconcile(Input(catalog, "virtual_machines:\n  vm1:\n    size: large", history));
        var signal = Assert.Single(blocked.Capabilities).Signals[0];
        Assert.DoesNotContain(signal.Actions, a => a.Identity.ActionKey == "update_vm");
        Assert.DoesNotContain(blocked.ToCreate, a => a.Identity.ActionKey == "update_vm");

        var historyWithDependency = history
            .Append(Succeeded(Id("virtual_machines", "vm1", "provision_vm"), SignalTrigger.ADD, "{\"size\":\"small\"}", T(1), false))
            .ToList();

        var unblocked = Reconciler.Reconcile(Input(catalog, "virtual_machines:\n  vm1:\n    size: large", historyWithDependency));
        var update = Assert.Single(unblocked.ToCreate);
        Assert.Equal(Id("virtual_machines", "vm1", "update_vm"), update.Identity);
    }

    // --- Live policy (never frozen on rows) ---------------------------------------------

    [Fact]
    public void Pending_manual_action_whose_policy_became_optional_stops_being_drift()
    {
        var history = new[]
        {
            Succeeded(Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, "true", T(1), false)
        };
        // The row was queued back when redeploy was MANUAL_REQUIRED.
        var live = new[]
        {
            new LiveActionRow(Guid.NewGuid(), Id("features.sso_login", null, "redeploy"), SignalTrigger.STATE, ActionStatus.QUEUED, "true")
        };

        var result = Reconciler.Reconcile(Input(
            SsoCatalog(redeployPolicy: "OPTIONAL"), "features:\n  sso_login: true", history, live));

        // The row is reused (no churn), shown under its live OPTIONAL policy, and the
        // capability is Active — the old "pending manual" reading is gone.
        Assert.Empty(result.ToCreate);
        Assert.Empty(result.ToSupersede);
        var capability = Assert.Single(result.Capabilities);
        Assert.Equal(CapabilityState.Active, capability.State);
        var redeploy = capability.Signals[0].Actions.Single(a => a.Identity.ActionKey == "redeploy");
        Assert.Equal(ActionPolicy.OPTIONAL, redeploy.Policy);
        Assert.Equal(live[0].Id, redeploy.LiveRowId);
    }

    [Fact]
    public void Optional_action_that_already_succeeded_counts_as_satisfied_when_it_becomes_manual()
    {
        var history = new[]
        {
            Succeeded(Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, "true", T(1), false),
            Succeeded(Id("features.sso_login", null, "redeploy"), SignalTrigger.STATE, "true", T(2), false)
        };

        var result = Reconciler.Reconcile(Input(
            SsoCatalog(redeployPolicy: "MANUAL_REQUIRED"), "features:\n  sso_login: true", history));

        Assert.Empty(result.ToCreate);
        Assert.Equal(CapabilityState.Active, Assert.Single(result.Capabilities).State);
    }

    [Fact]
    public void Optional_action_never_run_becomes_drift_when_it_becomes_manual()
    {
        var history = new[]
        {
            Succeeded(Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, "true", T(1), false)
        };

        var result = Reconciler.Reconcile(Input(
            SsoCatalog(redeployPolicy: "MANUAL_REQUIRED"), "features:\n  sso_login: true", history));

        var create = Assert.Single(result.ToCreate);
        Assert.Equal("redeploy", create.Identity.ActionKey);
        Assert.True(create.IsDrift);
        Assert.Equal(CapabilityState.Pending, Assert.Single(result.Capabilities).State);
    }

    [Fact]
    public void Value_flip_makes_the_other_rules_actions_required_and_only_those_visible()
    {
        var history = new[]
        {
            Succeeded(Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, "true", T(1), false),
            Succeeded(Id("features.sso_login", null, "redeploy"), SignalTrigger.STATE, "true", T(2), false)
        };

        var result = Reconciler.Reconcile(Input(SsoCatalog(), "features:\n  sso_login: false", history));

        // Only the when:false direction is emitted — never a mix of both directions.
        var actions = Assert.Single(result.Capabilities).Signals[0].Actions;
        Assert.All(actions, a => Assert.Equal("false", a.DesiredValueJson));
        Assert.Equal(2, result.ToCreate.Count);
        Assert.Contains(result.ToAutoStart, a => a.Identity.ActionKey == "configure_sso");
        // The old "true"-direction SUCCEEDED records don't satisfy the new value.
        Assert.All(actions, a => Assert.False(a.Satisfied));
    }

    // --- Collection items: add / delete / re-add ----------------------------------------

    [Fact]
    public void Item_added_then_removed_before_running_supersedes_cleanly_and_readd_starts_fresh()
    {
        // Step A: vm1 appears → one provision drift.
        var stepA = Reconciler.Reconcile(Input(VmCatalog(), "virtual_machines:\n  vm1:\n    size: small"));
        var provision = Assert.Single(stepA.ToCreate);
        Assert.Equal(Id("virtual_machines", "vm1", "provision_vm"), provision.Identity);

        // Step B: vm1 removed before anyone ran it → the queued row is superseded and,
        // because nothing was ever confirmed, there is no delete drift.
        var queuedRow = new LiveActionRow(Guid.NewGuid(), provision.Identity, SignalTrigger.ADD, ActionStatus.QUEUED, provision.DesiredValueJson);
        var stepB = Reconciler.Reconcile(Input(
            VmCatalog(), "virtual_machines: {}", live: new[] { queuedRow }));
        Assert.Equal(queuedRow.Id, Assert.Single(stepB.ToSupersede));
        Assert.Empty(stepB.ToCreate);

        // Step C: vm1 re-added → exactly one fresh drift, no accumulated pile.
        var stepC = Reconciler.Reconcile(Input(VmCatalog(), "virtual_machines:\n  vm1:\n    size: small"));
        Assert.Single(stepC.ToCreate);
    }

    [Fact]
    public void Removing_a_confirmed_item_requires_delete_actions()
    {
        var history = new[]
        {
            Succeeded(Id("virtual_machines", "vm1", "provision_vm"), SignalTrigger.ADD, "{\"size\":\"small\"}", T(1), false)
        };

        var result = Reconciler.Reconcile(Input(VmCatalog(), "virtual_machines: {}", history));

        var destroy = Assert.Single(result.ToCreate);
        Assert.Equal(Id("virtual_machines", "vm1", "destroy_vm"), destroy.Identity);
        Assert.Equal(SignalTrigger.DELETE, destroy.Trigger);
        Assert.Equal("null", destroy.DesiredValueJson);
        // Delete inputs resolve from the last confirmed body.
        Assert.Equal("vm1", destroy.ResolvedInputs["vm_name"]);
    }

    [Fact]
    public void Readding_a_confirmed_item_is_satisfied_and_supersedes_a_pending_delete()
    {
        var history = new[]
        {
            Succeeded(Id("virtual_machines", "vm1", "provision_vm"), SignalTrigger.ADD, "{\"size\":\"small\"}", T(1), false)
        };
        // The never-run cleanup queued while vm1 was absent.
        var pendingDestroy = new LiveActionRow(
            Guid.NewGuid(), Id("virtual_machines", "vm1", "destroy_vm"), SignalTrigger.DELETE, ActionStatus.QUEUED, "null");

        var result = Reconciler.Reconcile(Input(
            VmCatalog(), "virtual_machines:\n  vm1:\n    size: small", history, new[] { pendingDestroy }));

        // Net state matches the last confirmed state: nothing to do, the stale delete dies.
        Assert.Empty(result.ToCreate);
        Assert.Equal(pendingDestroy.Id, Assert.Single(result.ToSupersede));
        Assert.Equal(CapabilityState.Active, Assert.Single(result.Capabilities).State);
    }

    [Fact]
    public void Delete_success_newer_than_add_success_makes_a_readd_real_drift_again()
    {
        var history = new[]
        {
            Succeeded(Id("virtual_machines", "vm1", "provision_vm"), SignalTrigger.ADD, "{\"size\":\"small\"}", T(1), false),
            Succeeded(Id("virtual_machines", "vm1", "destroy_vm"), SignalTrigger.DELETE, "null", T(2), false)
        };

        var result = Reconciler.Reconcile(Input(VmCatalog(), "virtual_machines:\n  vm1:\n    size: small", history));

        // The provisioning was confirmed undone by the delete, so presence is drift again.
        var create = Assert.Single(result.ToCreate);
        Assert.Equal("provision_vm", create.Identity.ActionKey);
    }

    // --- Modify ---------------------------------------------------------------------------

    [Fact]
    public void Body_change_of_a_confirmed_item_is_modify_drift_but_never_gates_active()
    {
        var history = new[]
        {
            Succeeded(Id("virtual_machines", "vm1", "provision_vm"), SignalTrigger.ADD, "{\"size\":\"small\"}", T(1), false)
        };

        var result = Reconciler.Reconcile(Input(VmCatalog(), "virtual_machines:\n  vm1:\n    size: large", history));

        var update = Assert.Single(result.ToCreate);
        Assert.Equal(Id("virtual_machines", "vm1", "update_vm"), update.Identity);
        Assert.Equal(SignalTrigger.MODIFY, update.Trigger);
        Assert.Equal("{\"size\":\"large\"}", update.DesiredValueJson);

        var capability = Assert.Single(result.Capabilities);
        Assert.Equal(CapabilityState.Active, capability.State); // modify never gates
        Assert.True(capability.HasModifyDrift);
    }

    [Fact]
    public void Modify_does_not_refire_after_its_runbook_confirmed_the_new_body()
    {
        var history = new[]
        {
            Succeeded(Id("virtual_machines", "vm1", "provision_vm"), SignalTrigger.ADD, "{\"size\":\"small\"}", T(1), false),
            Succeeded(Id("virtual_machines", "vm1", "update_vm"), SignalTrigger.MODIFY, "{\"size\":\"large\"}", T(2), false)
        };

        var settled = Reconciler.Reconcile(Input(VmCatalog(), "virtual_machines:\n  vm1:\n    size: large", history));
        Assert.Empty(settled.ToCreate);
        Assert.False(Assert.Single(settled.Capabilities).HasModifyDrift);

        // A further body change fires modify again.
        var changedAgain = Reconciler.Reconcile(Input(VmCatalog(), "virtual_machines:\n  vm1:\n    size: xlarge", history));
        var update = Assert.Single(changedAgain.ToCreate);
        Assert.Equal(SignalTrigger.MODIFY, update.Trigger);
    }

    // --- FAILED parking -------------------------------------------------------------------

    [Fact]
    public void Failed_row_parks_the_drift_without_recreation_or_auto_retry()
    {
        var failed = new LiveActionRow(
            Guid.NewGuid(), Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, ActionStatus.FAILED, "true");

        var result = Reconciler.Reconcile(Input(SsoCatalog(redeployPolicy: "OPTIONAL"), "features:\n  sso_login: true", live: new[] { failed }));

        // AUTO drift exists, but the FAILED row covers the identity: no new row, no
        // auto-start (no hammering every minute), capability parked as Drifted.
        Assert.DoesNotContain(result.ToCreate, a => a.Identity.ActionKey == "configure_sso");
        Assert.DoesNotContain(result.ToAutoStart, a => a.Identity.ActionKey == "configure_sso");
        Assert.Empty(result.ToSupersede);
        Assert.Equal(CapabilityState.Drifted, Assert.Single(result.Capabilities).State);
    }

    [Fact]
    public void Failed_row_is_superseded_and_recreated_when_the_desired_snapshot_changes()
    {
        var failed = new LiveActionRow(
            Guid.NewGuid(), Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, ActionStatus.FAILED, "true");

        var result = Reconciler.Reconcile(Input(SsoCatalog(), "features:\n  sso_login: false", live: new[] { failed }));

        Assert.Contains(failed.Id, result.ToSupersede);
        Assert.Contains(result.ToCreate, a => a.Identity.ActionKey == "configure_sso" && a.DesiredValueJson == "false");
        Assert.Contains(result.ToAutoStart, a => a.Identity.ActionKey == "configure_sso");
    }

    [Fact]
    public void Running_rows_are_left_alone_and_not_duplicated()
    {
        var running = new LiveActionRow(
            Guid.NewGuid(), Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, ActionStatus.RUNNING, "true");

        var result = Reconciler.Reconcile(Input(SsoCatalog(redeployPolicy: "OPTIONAL"), "features:\n  sso_login: true", live: new[] { running }));

        Assert.DoesNotContain(result.ToCreate, a => a.Identity.ActionKey == "configure_sso");
        Assert.DoesNotContain(result.ToAutoStart, a => a.Identity.ActionKey == "configure_sso");
        Assert.Empty(result.ToSupersede);
    }

    // --- Per-instance overrides ---------------------------------------------------------------

    [Fact]
    public void Instance_override_rule_applies_only_to_its_item_key()
    {
        var overrides = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    item_key: vm-quirky
                    actions:
                      - key: apply_quirk_profile
                        runbook: acme/apply-quirk-profile
                        label: "Apply quirk profile"
                        policy: MANUAL_REQUIRED
                        inputs:
                          vm_name: { from: key }
            """);

        var result = Reconciler.Reconcile(Input(
            VmCatalog(overrides), "virtual_machines:\n  vm1:\n    size: small\n  vm-quirky:\n    size: small"));

        // Both VMs need provisioning; only vm-quirky additionally needs the quirk profile.
        Assert.Contains(result.ToCreate, a => a.Identity == Id("virtual_machines", "vm1", "provision_vm"));
        Assert.Contains(result.ToCreate, a => a.Identity == Id("virtual_machines", "vm-quirky", "provision_vm"));
        Assert.Contains(result.ToCreate, a => a.Identity == Id("virtual_machines", "vm-quirky", "apply_quirk_profile"));
        Assert.DoesNotContain(result.ToCreate, a => a.Identity == Id("virtual_machines", "vm1", "apply_quirk_profile"));
    }

    // --- Scalar lists ---------------------------------------------------------------------

    [Fact]
    public void Scalar_list_values_are_their_own_identity_keys()
    {
        var result = Reconciler.Reconcile(Input(QuirksCatalog(), "quirks:\n  - flaky_dns"));

        // OPTIONAL add action: available (needs a live row) but never drift.
        var investigate = Assert.Single(result.ToCreate);
        Assert.Equal(Id("quirks", "flaky_dns", "investigate_quirk"), investigate.Identity);
        Assert.False(investigate.IsDrift);
        Assert.Equal("flaky_dns", investigate.ResolvedInputs["quirk"]);
        Assert.Equal(CapabilityState.Active, Assert.Single(result.Capabilities).State);
    }

    [Fact]
    public void Removing_a_confirmed_scalar_item_fires_the_auto_delete_action()
    {
        var history = new[]
        {
            Succeeded(Id("quirks", "flaky_dns", "investigate_quirk"), SignalTrigger.ADD, "\"flaky_dns\"", T(1), false)
        };

        var result = Reconciler.Reconcile(Input(QuirksCatalog(), "quirks: []", history));

        var resolved = Assert.Single(result.ToCreate);
        Assert.Equal(Id("quirks", "flaky_dns", "log_quirk_resolved"), resolved.Identity);
        Assert.Contains(result.ToAutoStart, a => a.Identity.ActionKey == "log_quirk_resolved");
    }

    [Fact]
    public void Removing_a_never_confirmed_scalar_item_requires_nothing()
    {
        var live = new[]
        {
            new LiveActionRow(Guid.NewGuid(), Id("quirks", "flaky_dns", "investigate_quirk"), SignalTrigger.ADD, ActionStatus.QUEUED, "\"flaky_dns\"")
        };

        var result = Reconciler.Reconcile(Input(
            QuirksCatalog(), "quirks: []", live: live));

        Assert.Empty(result.ToCreate);
        Assert.Equal(live[0].Id, Assert.Single(result.ToSupersede));
    }

    // --- Validation -------------------------------------------------------------------------

    [Fact]
    public void Object_array_under_a_keyed_collection_is_a_validation_error_not_a_guess()
    {
        var result = Reconciler.Reconcile(Input(
            VmCatalog(), "virtual_machines:\n  - name: vm1\n    size: small"));

        Assert.NotEmpty(result.ValidationErrors);
        Assert.Contains("keyed maps", result.ValidationErrors[0]);
        Assert.Empty(result.ToCreate);
        var signal = Assert.Single(result.Capabilities).Signals[0];
        Assert.NotNull(signal.ValidationError);
    }

    // --- Visibility / states ------------------------------------------------------------------

    [Fact]
    public void Capability_is_not_visible_when_a_signal_path_is_absent()
    {
        var result = Reconciler.Reconcile(Input(VmCatalog(), "features:\n  sso_login: true"));

        var capability = Assert.Single(result.Capabilities);
        Assert.Equal(CapabilityState.NotVisible, capability.State);
        Assert.Empty(result.ToCreate);
    }

    [Fact]
    public void Empty_container_and_false_both_count_as_present()
    {
        var vms = Reconciler.Reconcile(Input(VmCatalog(), "virtual_machines: {}"));
        Assert.NotEqual(CapabilityState.NotVisible, Assert.Single(vms.Capabilities).State);

        var sso = Reconciler.Reconcile(Input(SsoCatalog(), "features:\n  sso_login: false", new[]
        {
            Succeeded(Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, "false", T(1), false),
            Succeeded(Id("features.sso_login", null, "redeploy"), SignalTrigger.STATE, "false", T(2), false)
        }));
        // Off, and the off-state is confirmed → Disabled, not Pending.
        Assert.Equal(CapabilityState.Disabled, Assert.Single(sso.Capabilities).State);
    }

    // --- Idempotence ----------------------------------------------------------------------------

    [Fact]
    public void Applying_the_verdict_and_reconciling_again_changes_nothing()
    {
        var input = Reconciler.Reconcile(Input(
            SsoCatalog(), "features:\n  sso_login: true",
            new[] { Succeeded(Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, "true", T(1), false) }));

        // Materialize ToCreate as live QUEUED rows, exactly as the runner does.
        var live = input.ToCreate
            .Select(a => new LiveActionRow(Guid.NewGuid(), a.Identity, a.Trigger, ActionStatus.QUEUED, a.DesiredValueJson))
            .ToList();

        var second = Reconciler.Reconcile(Input(
            SsoCatalog(), "features:\n  sso_login: true",
            new[] { Succeeded(Id("features.sso_login", null, "configure_sso"), SignalTrigger.STATE, "true", T(1), false) },
            live));

        Assert.Empty(second.ToCreate);
        Assert.Empty(second.ToSupersede);
        Assert.Empty(second.ToAdopt);
    }

    // --- executor / secret inputs ----------------------------------------------------------

    private static CapabilityCatalog DockerVmCatalog()
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            title: "Virtual Machines"
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: run_ansible_profile
                        runbook: homelab-ops/ansible-profile
                        policy: MANUAL_REQUIRED
                        executor: docker
                        docker:
                          image: "homelab/toolbox:latest"
                          command: ["ansible-profile"]
                        inputs:
                          docker_host: { from: item.private_ip }
                          registry_token: { secret: "Homelab/environments/REGISTRY_PULL_TOKEN" }
            """);
        return CapabilityCatalogLoader.Merge("acme", new[] { capability }, Array.Empty<CapabilityDefinition>());
    }

    [Fact]
    public void Docker_executor_and_secret_inputs_propagate_unresolved_into_the_required_action()
    {
        var result = Reconciler.Reconcile(Input(DockerVmCatalog(), """
            virtual_machines:
              vm-alpha-01:
                private_ip: "192.168.1.10"
            """));

        var action = Assert.Single(result.ToCreate);
        Assert.Equal(ExecutorKind.Docker, action.ExecutorKind);
        Assert.NotNull(action.DockerConfig);
        Assert.Equal("homelab/toolbox:latest", action.DockerConfig!.Image);
        Assert.Equal(new[] { "ansible-profile" }, action.DockerConfig.Command);

        // The pure Reconciler never resolves a secret — it only carries the reference.
        var secret = Assert.Single(action.SecretInputs);
        Assert.Equal("registry_token", secret.Name);
        Assert.Equal("Homelab/environments/REGISTRY_PULL_TOKEN", secret.SecretRef);
        Assert.DoesNotContain("registry_token", action.ResolvedInputs.Keys);

        Assert.Equal("192.168.1.10", action.ResolvedInputs["docker_host"]);
    }

    [Fact]
    public void Shell_actions_default_to_shell_executor_with_no_docker_config()
    {
        var result = Reconciler.Reconcile(Input(SsoCatalog(), "features:\n  sso_login: true"));

        Assert.All(result.ToCreate, a =>
        {
            Assert.Equal(ExecutorKind.Shell, a.ExecutorKind);
            Assert.Null(a.DockerConfig);
            Assert.Empty(a.SecretInputs);
        });
    }

    // --- executor config snapshot --------------------------------------------------------

    private static CapabilityCatalog PlaybookCatalog(string? fingerprint)
    {
        var catalog = CapabilityCatalogLoader.Merge("homelab", new[] { CapabilityCatalogLoader.LoadCapability("""
            capability: probes
            signals:
              - path: checks
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: probe
                        runbook: homelab-ops/probe
                        executor: docker
                        docker:
                          build:
                            context: playbooks/probe
            """) }, Array.Empty<CapabilityDefinition>());

        // What FileCapabilityCatalogProvider does at load time.
        var action = catalog.Capabilities[0].Signals[0].Rules[0].Actions[0];
        action.Docker = action.Docker! with { Build = action.Docker.Build! with { Fingerprint = fingerprint } };
        return catalog;
    }

    [Fact]
    public void A_queued_row_pinned_to_the_current_playbook_fingerprint_is_kept()
    {
        var first = Assert.Single(Reconciler.Reconcile(Input(PlaybookCatalog("aaa"), "checks:\n  web: {}")).ToCreate);
        var row = new LiveActionRow(Guid.NewGuid(), first.Identity, SignalTrigger.ADD, ActionStatus.QUEUED,
            first.DesiredValueJson, ExecutorConfigJson.Serialize(first.DockerConfig));

        var again = Reconciler.Reconcile(Input(PlaybookCatalog("aaa"), "checks:\n  web: {}", live: new[] { row }));

        Assert.Empty(again.ToSupersede);
        Assert.Empty(again.ToCreate);
    }

    [Theory]
    [InlineData("QUEUED")]
    [InlineData("FAILED")]
    public void A_changed_playbook_fingerprint_supersedes_the_row_and_emits_a_fresh_one(string status)
    {
        var first = Assert.Single(Reconciler.Reconcile(Input(PlaybookCatalog("aaa"), "checks:\n  web: {}")).ToCreate);
        var row = new LiveActionRow(Guid.NewGuid(), first.Identity, SignalTrigger.ADD, Enum.Parse<ActionStatus>(status),
            first.DesiredValueJson, ExecutorConfigJson.Serialize(first.DockerConfig));

        var changed = Reconciler.Reconcile(Input(PlaybookCatalog("bbb"), "checks:\n  web: {}", live: new[] { row }));

        Assert.Equal(row.Id, Assert.Single(changed.ToSupersede));
        var fresh = Assert.Single(changed.ToCreate);
        Assert.Equal("bbb", fresh.DockerConfig!.Build!.Fingerprint);
    }

    [Fact]
    public void A_running_row_is_never_superseded_by_a_playbook_change()
    {
        var first = Assert.Single(Reconciler.Reconcile(Input(PlaybookCatalog("aaa"), "checks:\n  web: {}")).ToCreate);
        var row = new LiveActionRow(Guid.NewGuid(), first.Identity, SignalTrigger.ADD, ActionStatus.RUNNING,
            first.DesiredValueJson, ExecutorConfigJson.Serialize(first.DockerConfig));

        var changed = Reconciler.Reconcile(Input(PlaybookCatalog("bbb"), "checks:\n  web: {}", live: new[] { row }));

        Assert.Empty(changed.ToSupersede);
        Assert.Empty(changed.ToCreate);
    }

    // --- nested collections (containers inside VMs) ---------------------------------------

    private static CapabilityCatalog VmWithContainersCatalog()
    {
        var vms = CapabilityCatalogLoader.LoadCapability("""
            capability: vms
            signals:
              - path: proxmox.vms
                kind: keyed_collection
                exclude: [containers]
                rules:
                  - on: add
                    actions:
                      - key: apply_vm
                        runbook: ops/apply
                        inputs:
                          vm: { from: item }
                          vms: { from: collection }
                      - key: configure_vm
                        runbook: ops/configure
                        depends_on: [ "proxmox.vms.apply_vm" ]
                  - on: modify
                    actions:
                      - key: resize_vm
                        runbook: ops/resize
                  - on: delete
                    actions:
                      - key: destroy_vm
                        runbook: ops/destroy
            """);
        var containers = CapabilityCatalogLoader.LoadCapability("""
            capability: containers
            signals:
              - path: proxmox.vms.*.containers
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: deploy
                        runbook: ops/deploy
                        depends_on: [ "proxmox.vms.configure_vm" ]
                        inputs:
                          name: { from: key }
                          host: { from: parent.ip }
                          vm: { from: parent_key }
                  - on: modify
                    actions:
                      - key: update
                        runbook: ops/update
                  - on: delete
                    actions:
                      - key: remove
                        runbook: ops/remove
                        inputs:
                          host: { from: parent.ip }
            """);
        return CapabilityCatalogLoader.Merge("homelab", new[] { vms, containers }, Array.Empty<CapabilityDefinition>());
    }

    private const string OneVmTwoContainers = """
        proxmox:
          vms:
            vm1:
              ip: "10.0.0.5"
              cores: 2
              containers:
                web: { image: "nginx:1" }
                db: { image: "postgres:16" }
        """;

    private static SucceededRecord Done(string path, string key, string action, SignalTrigger trigger, string? body, int minute)
        => Succeeded(Id(path, key, action), trigger, body, T(minute), false);

    [Fact]
    public void Containers_wait_for_their_vm_and_are_excluded_from_its_body()
    {
        var first = Reconciler.Reconcile(Input(VmWithContainersCatalog(), OneVmTwoContainers));

        var apply = Assert.Single(first.ToCreate);
        Assert.Equal("apply_vm", apply.Identity.ActionKey);
        Assert.DoesNotContain("containers", apply.DesiredValueJson);
        Assert.DoesNotContain("containers", apply.ResolvedInputs["vms"]);
        Assert.Contains("\"vm1\"", apply.ResolvedInputs["vms"]);
    }

    [Fact]
    public void Once_the_vm_is_configured_each_container_gets_its_own_deploy_with_parent_inputs()
    {
        var vmBody = Assert.Single(Reconciler.Reconcile(Input(VmWithContainersCatalog(), OneVmTwoContainers)).ToCreate).DesiredValueJson;
        var history = new[]
        {
            Done("proxmox.vms", "vm1", "apply_vm", SignalTrigger.ADD, vmBody, 1),
            Done("proxmox.vms", "vm1", "configure_vm", SignalTrigger.ADD, vmBody, 2)
        };

        var result = Reconciler.Reconcile(Input(VmWithContainersCatalog(), OneVmTwoContainers, history));

        var deploys = result.ToCreate.Where(a => a.Identity.ActionKey == "deploy").OrderBy(a => a.Identity.ItemKey).ToList();
        Assert.Equal(new[] { "vm1/db", "vm1/web" }, deploys.Select(d => d.Identity.ItemKey));
        Assert.Equal("web", deploys[1].ResolvedInputs["name"]);
        Assert.Equal("10.0.0.5", deploys[1].ResolvedInputs["host"]);
        Assert.Equal("vm1", deploys[1].ResolvedInputs["vm"]);
        Assert.DoesNotContain(result.ToCreate, a => a.Identity.ActionKey == "resize_vm");
    }

    [Fact]
    public void Editing_a_container_updates_that_container_only_and_removing_one_removes_it()
    {
        var vmBody = Assert.Single(Reconciler.Reconcile(Input(VmWithContainersCatalog(), OneVmTwoContainers)).ToCreate).DesiredValueJson;
        var history = new[]
        {
            Done("proxmox.vms", "vm1", "apply_vm", SignalTrigger.ADD, vmBody, 1),
            Done("proxmox.vms", "vm1", "configure_vm", SignalTrigger.ADD, vmBody, 2),
            Done("proxmox.vms.*.containers", "vm1/web", "deploy", SignalTrigger.ADD, "{\"image\":\"nginx:1\"}", 3),
            Done("proxmox.vms.*.containers", "vm1/db", "deploy", SignalTrigger.ADD, "{\"image\":\"postgres:16\"}", 3)
        };

        var edited = OneVmTwoContainers.Replace("nginx:1", "nginx:2").Replace("\n        db: { image: \"postgres:16\" }", "");
        var result = Reconciler.Reconcile(Input(VmWithContainersCatalog(), edited, history));

        Assert.Equal(new[] { "remove:vm1/db", "update:vm1/web" },
            result.ToCreate.Select(a => $"{a.Identity.ActionKey}:{a.Identity.ItemKey}").Order());
        Assert.Equal("10.0.0.5", result.ToCreate.Single(a => a.Identity.ActionKey == "remove").ResolvedInputs["host"]);
    }

    [Fact]
    public void A_recreated_vm_needs_its_containers_deployed_again_and_a_vanished_vm_skips_container_removal()
    {
        var vmBody = Assert.Single(Reconciler.Reconcile(Input(VmWithContainersCatalog(), OneVmTwoContainers)).ToCreate).DesiredValueJson;
        var deployed = new[]
        {
            Done("proxmox.vms", "vm1", "apply_vm", SignalTrigger.ADD, vmBody, 1),
            Done("proxmox.vms", "vm1", "configure_vm", SignalTrigger.ADD, vmBody, 2),
            Done("proxmox.vms.*.containers", "vm1/web", "deploy", SignalTrigger.ADD, "{\"image\":\"nginx:1\"}", 3),
            Done("proxmox.vms.*.containers", "vm1/db", "deploy", SignalTrigger.ADD, "{\"image\":\"postgres:16\"}", 3)
        };

        // VM removed from the inventory: only the VM destroy, no per-container removals.
        var gone = Reconciler.Reconcile(Input(VmWithContainersCatalog(), "proxmox:\n  vms: {}", deployed));
        Assert.Equal(new[] { "destroy_vm" }, gone.ToCreate.Select(a => a.Identity.ActionKey));

        // Destroyed, then added back and re-provisioned: the old configure/deploys belong to
        // the previous VM, so containers wait for the new configure and then deploy again.
        var recreated = deployed.Append(Done("proxmox.vms", "vm1", "destroy_vm", SignalTrigger.DELETE, "null", 4))
            .Append(Done("proxmox.vms", "vm1", "apply_vm", SignalTrigger.ADD, vmBody, 5)).ToList();
        var waiting = Reconciler.Reconcile(Input(VmWithContainersCatalog(), OneVmTwoContainers, recreated));
        Assert.Equal(new[] { "configure_vm" }, waiting.ToCreate.Select(a => a.Identity.ActionKey));

        recreated.Add(Done("proxmox.vms", "vm1", "configure_vm", SignalTrigger.ADD, vmBody, 6));
        var redeploy = Reconciler.Reconcile(Input(VmWithContainersCatalog(), OneVmTwoContainers, recreated));
        Assert.Equal(new[] { "deploy:vm1/db", "deploy:vm1/web" },
            redeploy.ToCreate.Select(a => $"{a.Identity.ActionKey}:{a.Identity.ItemKey}").Order());
    }

    // --- file-backed lists (compose files listed per VM) ----------------------------------

    private static CapabilityCatalog StacksCatalog(Dictionary<string, string> files)
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: stacks
            signals:
              - path: vms.*.compose
                kind: scalar_list
                files: stacks
                rules:
                  - on: add
                    actions:
                      - key: deploy
                        runbook: ops/deploy
                        inputs:
                          file: { from: item.file }
                          content: { from: item.content }
                          host: { from: parent.ip }
                  - on: modify
                    actions:
                      - key: update
                        runbook: ops/update
                  - on: delete
                    actions:
                      - key: remove
                        runbook: ops/remove
                        inputs:
                          content: { from: item.content }
            """);
        capability.Signals[0].FileIndex = files; // what FileCapabilityCatalogProvider stamps
        return CapabilityCatalogLoader.Merge("homelab", new[] { capability }, Array.Empty<CapabilityDefinition>());
    }

    private static readonly Dictionary<string, string> StackFiles = new()
    {
        ["home/glance.yml"] = "name: glance\n",
        ["home/immich.yml"] = "name: immich\n",
        ["network/pihole.yml"] = "name: pihole\n"
    };

    [Fact]
    public void File_lists_expand_globs_into_one_item_per_file_carrying_its_content()
    {
        var result = Reconciler.Reconcile(Input(StacksCatalog(StackFiles), """
            vms:
              node1:
                ip: "10.0.0.7"
                compose: ["home/*", "network/pihole.yml"]
            """));

        Assert.Empty(result.ValidationErrors);
        Assert.Equal(new[] { "node1/home/glance.yml", "node1/home/immich.yml", "node1/network/pihole.yml" },
            result.ToCreate.Select(a => a.Identity.ItemKey).Order());
        var pihole = result.ToCreate.Single(a => a.Identity.ItemKey == "node1/network/pihole.yml");
        Assert.Equal("network/pihole.yml", pihole.ResolvedInputs["file"]);
        Assert.Equal("name: pihole\n", pihole.ResolvedInputs["content"]);
        Assert.Equal("10.0.0.7", pihole.ResolvedInputs["host"]);
    }

    [Fact]
    public void Editing_a_listed_file_updates_it_and_unlisting_one_removes_it_with_its_last_content()
    {
        const string yaml = """
            vms:
              node1:
                ip: "10.0.0.7"
                compose: ["home/*"]
            """;
        var first = Reconciler.Reconcile(Input(StacksCatalog(StackFiles), yaml));
        var history = first.ToCreate.Select(a => Succeeded(a.Identity, SignalTrigger.ADD, a.DesiredValueJson, T(1), false)).ToList();

        var edited = new Dictionary<string, string>(StackFiles) { ["home/glance.yml"] = "name: glance\nservices: {}\n" };
        var result = Reconciler.Reconcile(Input(StacksCatalog(edited), yaml.Replace("[\"home/*\"]", "[\"home/glance.yml\"]"), history));

        Assert.Equal(new[] { "remove:node1/home/immich.yml", "update:node1/home/glance.yml" },
            result.ToCreate.Select(a => $"{a.Identity.ActionKey}:{a.Identity.ItemKey}").Order());
        Assert.Equal("name: immich\n", result.ToCreate.Single(a => a.Identity.ActionKey == "remove").ResolvedInputs["content"]);
    }

    [Fact]
    public void A_listed_path_that_is_not_a_file_is_a_validation_error()
    {
        var result = Reconciler.Reconcile(Input(StacksCatalog(StackFiles), """
            vms:
              node1:
                compose: ["home/nope.yml"]
            """));

        Assert.Contains(result.ValidationErrors, e => e.Contains("'home/nope.yml' is not a file under 'stacks/'"));
        Assert.Empty(result.ToCreate);
    }
}
