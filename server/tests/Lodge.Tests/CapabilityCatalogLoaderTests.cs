using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Xunit;

namespace Lodge.Tests;

public class CapabilityCatalogLoaderTests
{
    private const string SsoYaml = """
        capability: sso
        title: "Single Sign-On"
        description: "Instance sign-in via corporate SSO."
        signals:
          - path: features.sso_login
            rules:
              - when: true
                actions:
                  - key: configure_sso
                    executor: http
                    http: { url: "https://ops.test/acme-instance-ops/configure-sso" }
                    label: "Configure SSO"
                    policy: AUTO
                    inputs:
                      instance: { from: instance }
                      mode: { const: "enable" }
                  - key: redeploy
                    executor: http
                    http: { url: "https://ops.test/acme-instance-ops/redeploy" }
                    policy: AUTO
                    inputs:
                      instance: { from: instance }
                      environment: { prompt: "Target environment", required: true }
              - when: false
                actions:
                  - key: configure_sso
                    executor: http
                    http: { url: "https://ops.test/acme-instance-ops/configure-sso" }
                    label: "Disable SSO"
                    policy: AUTO
                    inputs:
                      mode: { const: "disable" }
        """;

    [Fact]
    public void LoadCapability_parses_state_rules_with_canonical_when_values()
    {
        var capability = CapabilityCatalogLoader.LoadCapability(SsoYaml, "sso.yaml");

        Assert.Equal("sso", capability.Code);
        Assert.Equal("Single Sign-On", capability.Title);
        Assert.Equal("sso.yaml", capability.SourceFile);

        var signal = Assert.Single(capability.Signals);
        Assert.Equal("features.sso_login", signal.Path);
        Assert.Equal(SignalKind.Scalar, signal.Kind);
        Assert.Equal(2, signal.Rules.Count);

        // `when` guards are canonical JSON booleans, matching what the reconciler
        // derives from instance YAML — never the strings "true"/"false".
        Assert.Equal("true", signal.Rules[0].WhenJson);
        Assert.Equal("false", signal.Rules[1].WhenJson);
        Assert.All(signal.Rules, r => Assert.Equal(SignalTrigger.STATE, r.Trigger));
    }

    [Fact]
    public void LoadCapability_parses_actions_policies_and_inputs()
    {
        var capability = CapabilityCatalogLoader.LoadCapability(SsoYaml);
        var enableRule = capability.Signals[0].Rules[0];

        Assert.Equal(2, enableRule.Actions.Count);

        var configure = enableRule.Actions[0];
        Assert.Equal("configure_sso", configure.Key);
        Assert.Equal(ExecutorKind.Http, configure.ExecutorKind);
        Assert.Equal("https://ops.test/acme-instance-ops/configure-sso", configure.Http!.Url);
        Assert.Null(configure.Requires);
        Assert.Equal("Configure SSO", configure.Label);
        Assert.Equal(ActionPolicy.AUTO, configure.Policy);
        Assert.Contains(configure.Inputs, i => i.Name == "instance" && i.Kind == RuleInputKind.From && i.Value == "instance");
        Assert.Contains(configure.Inputs, i => i.Name == "mode" && i.Kind == RuleInputKind.Const && i.Value == "enable");

        var redeploy = enableRule.Actions[1];
        Assert.Equal("redeploy", redeploy.Label); // label defaults to the key
        Assert.Contains(redeploy.Inputs, i => i.Name == "environment" && i.Kind == RuleInputKind.Prompt && i.Required);
    }

    [Fact]
    public void LoadCapability_rejects_missing_capability_code()
    {
        Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability("title: Oops", "broken.yaml"));
    }

    [Fact]
    public void LoadCapability_rejects_missing_action_key()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { executor: http, http: { url: "https://ops.test/r" } } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("key", ex.Message);
    }

    [Fact]
    public void LoadCapability_rejects_on_rules_for_scalar_signals_and_when_rules_for_collections()
    {
        var onForScalar = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - on: add
                    actions: [ { key: k, executor: http, http: { url: "https://ops.test/r" } } ]
            """;
        Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(onForScalar));

        var whenForCollection = """
            capability: bad
            signals:
              - path: vms
                kind: keyed_collection
                rules:
                  - when: true
                    actions: [ { key: k, executor: http, http: { url: "https://ops.test/r" } } ]
            """;
        Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(whenForCollection));
    }

    [Fact]
    public void LoadCapability_rejects_modify_for_scalar_lists()
    {
        var yaml = """
            capability: quirks
            signals:
              - path: quirks
                kind: scalar_list
                rules:
                  - on: modify
                    actions: [ { key: k, executor: http, http: { url: "https://ops.test/r" } } ]
            """;
        Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
    }

    [Fact]
    public void LoadCapability_rejects_same_key_under_two_triggers_of_one_signal()
    {
        var yaml = """
            capability: vms
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions: [ { key: provision, executor: http, http: { url: "https://ops.test/acme/provision" } } ]
                  - on: modify
                    actions: [ { key: provision, executor: http, http: { url: "https://ops.test/acme/update" } } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("distinct key per trigger", ex.Message);
    }

    [Fact]
    public void LoadCapability_rejects_duplicate_key_within_the_same_rule()
    {
        var yaml = """
            capability: vms
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - { key: provision, executor: http, http: { url: "https://ops.test/acme/provision" } }
                      - { key: provision, executor: http, http: { url: "https://ops.test/acme/provision-2" } }
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("used more than once", ex.Message);
    }

    [Fact]
    public void LoadCapability_allows_the_same_key_across_state_when_directions()
    {
        // The Sso fixture already reuses "configure_sso" across when:true/when:false —
        // this is the intended identity-stability behavior, not a collision.
        var capability = CapabilityCatalogLoader.LoadCapability(SsoYaml);
        Assert.Equal("configure_sso", capability.Signals[0].Rules[0].Actions[0].Key);
        Assert.Equal("configure_sso", capability.Signals[0].Rules[1].Actions[0].Key);
    }

    [Fact]
    public void LoadCapability_rejects_auto_actions_with_prompts()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: k
                        executor: http
                        http: { url: "https://ops.test/r" }
                        policy: AUTO
                        inputs:
                          env: { prompt: "Which env?" }
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("AUTO", ex.Message);
    }

    [Fact]
    public void Merge_appends_instance_override_rules_to_the_matching_signal()
    {
        var generic = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions: [ { key: provision, executor: http, http: { url: "https://ops.test/acme/provision" } } ]
            """);
        var overrides = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    item_key: vm-quirky
                    actions: [ { key: apply_quirk_profile, executor: http, http: { url: "https://ops.test/acme/apply-quirk-profile" } } ]
            """);

        var catalog = CapabilityCatalogLoader.Merge("acme", new[] { generic }, new[] { overrides });

        var capability = Assert.Single(catalog.Capabilities);
        var signal = Assert.Single(capability.Signals);
        Assert.Equal(2, signal.Rules.Count);
        Assert.Null(signal.Rules[0].ItemKey);              // generic rule untouched
        Assert.Equal("vm-quirky", signal.Rules[1].ItemKey); // override appended, key-scoped
    }

    [Fact]
    public void Merge_does_not_mutate_the_cached_generic_definitions()
    {
        var generic = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions: [ { key: provision, executor: http, http: { url: "https://ops.test/acme/provision" } } ]
            """);
        var overrides = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: delete
                    actions: [ { key: destroy, executor: http, http: { url: "https://ops.test/acme/destroy" } } ]
            """);

        CapabilityCatalogLoader.Merge("acme", new[] { generic }, new[] { overrides });
        CapabilityCatalogLoader.Merge("acme", new[] { generic }, new[] { overrides });

        Assert.Single(generic.Signals[0].Rules); // still exactly the generic rule
    }

    [Fact]
    public void Merge_adds_unknown_override_capabilities_and_signals()
    {
        var generic = CapabilityCatalogLoader.LoadCapability("""
            capability: sso
            signals:
              - path: features.sso_login
                rules:
                  - when: true
                    actions: [ { key: configure, executor: http, http: { url: "https://ops.test/acme/configure" } } ]
            """);
        var overrides = CapabilityCatalogLoader.LoadCapability("""
            capability: special
            signals:
              - path: special.flag
                rules:
                  - when: true
                    actions: [ { key: special, executor: http, http: { url: "https://ops.test/acme/special" } } ]
            """);

        var catalog = CapabilityCatalogLoader.Merge("acme", new[] { generic }, new[] { overrides });

        Assert.Equal(2, catalog.Capabilities.Count);
        Assert.Contains(catalog.Capabilities, c => c.Code == "special");
    }

    // --- depends_on -------------------------------------------------------------------

    [Fact]
    public void Merge_resolves_depends_on_and_lets_a_valid_reference_through()
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions: [ { key: provision, executor: http, http: { url: "https://ops.test/acme/provision" } } ]
                  - on: modify
                    actions:
                      - key: update
                        executor: http
                        http: { url: "https://ops.test/acme/update" }
                        depends_on: [ "virtual_machines.provision" ]
            """);

        var catalog = CapabilityCatalogLoader.Merge("acme", new[] { capability }, Array.Empty<CapabilityDefinition>());

        Assert.Equal(2, catalog.Capabilities.Single().Signals.Single().Rules.Count);
    }

    [Fact]
    public void Merge_rejects_depends_on_referencing_an_unknown_action_key()
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: provision
                        executor: http
                        http: { url: "https://ops.test/acme/provision" }
                        depends_on: [ "virtual_machines.nonexistent" ]
            """);

        var ex = Assert.Throws<CatalogFormatException>(
            () => CapabilityCatalogLoader.Merge("acme", new[] { capability }, Array.Empty<CapabilityDefinition>()));
        Assert.Contains("does not match any known action key", ex.Message);
    }

    [Fact]
    public void Merge_rejects_depends_on_with_an_item_bracket_or_universal_marker()
    {
        var bracket = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions: [ { key: provision, executor: http, http: { url: "https://ops.test/acme/provision" } } ]
                  - on: modify
                    actions:
                      - key: update
                        executor: http
                        http: { url: "https://ops.test/acme/update" }
                        depends_on: [ "virtual_machines[vm1].provision" ]
            """);
        var ex = Assert.Throws<CatalogFormatException>(
            () => CapabilityCatalogLoader.Merge("acme", new[] { bracket }, Array.Empty<CapabilityDefinition>()));
        Assert.Contains("no item bracket", ex.Message);
    }

    [Fact]
    public void Merge_rejects_a_depends_on_cycle()
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: a
                        executor: http
                        http: { url: "https://ops.test/acme/a" }
                        depends_on: [ "virtual_machines.b" ]
                  - on: modify
                    actions:
                      - key: b
                        executor: http
                        http: { url: "https://ops.test/acme/b" }
                        depends_on: [ "virtual_machines.a" ]
            """);

        var ex = Assert.Throws<CatalogFormatException>(
            () => CapabilityCatalogLoader.Merge("acme", new[] { capability }, Array.Empty<CapabilityDefinition>()));
        Assert.Contains("cycle", ex.Message);
    }

    // --- executor -----------------------------------------------------------------------

    [Fact]
    public void LoadCapability_requires_an_explicit_executor()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("missing 'executor'", ex.Message);
    }

    [Fact]
    public void LoadCapability_rejects_the_retired_runbook_field()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k, runbook: ops/k, executor: http, http: { url: "https://ops.test/k" } } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("'runbook' is retired", ex.Message);
    }

    [Fact]
    public void LoadCapability_parses_an_http_executor_config()
    {
        var yaml = """
            capability: notify
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: notify
                        executor: http
                        requires: ops
                        http:
                          method: put
                          url: "https://hooks.test/{{ instance }}"
                          headers: { Authorization: "Bearer {{ token }}" }
                          query: { zone: "{{ zone }}" }
                          body: { name: "{{ instance }}", ttl: 1, tags: [a, b] }
                          timeout_seconds: 10
                          expect_status: [200, 204]
            """;
        var action = CapabilityCatalogLoader.LoadCapability(yaml).Signals[0].Rules[0].Actions[0];

        Assert.Equal("ops", action.Requires);
        var http = action.Http!;
        Assert.Equal("PUT", http.Method);
        Assert.Equal("https://hooks.test/{{ instance }}", http.Url);
        Assert.Equal("Bearer {{ token }}", http.Headers!["Authorization"]);
        Assert.Equal("{{ zone }}", http.Query!["zone"]);
        Assert.True(http.BodyIsJson);
        Assert.Contains("\"name\":\"{{ instance }}\"", http.Body);
        Assert.Contains("\"tags\":[\"a\",\"b\"]", http.Body);
        Assert.Equal(10, http.TimeoutSeconds);
        Assert.Equal(new[] { 200, 204 }, http.ExpectStatus);
        Assert.Null(action.Container);
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("Nobody")]
    [InlineData("")]
    public void LoadCapability_treats_requires_nobody_as_anyone(string requires)
    {
        var yaml = $$"""
            capability: c
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k, requires: "{{requires}}", executor: http, http: { url: "https://ops.test/k" } } ]
            """;
        Assert.Null(CapabilityCatalogLoader.LoadCapability(yaml).Signals[0].Rules[0].Actions[0].Requires);
    }

    [Fact]
    public void LoadCapability_rejects_an_http_url_that_is_not_absolute()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k, executor: http, http: { url: "/relative" } } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("absolute http(s) URL", ex.Message);
    }

    // --- views ----------------------------------------------------------------------------

    [Fact]
    public void A_capability_without_rules_is_a_view_and_may_use_per_item_scalar_paths()
    {
        var view = CapabilityCatalogLoader.LoadCapability("""
            capability: vm_sizes
            title: "VM sizes"
            signals:
              - path: proxmox.virtual_machines.*.cores
                label: CPU
              - path: proxmox.virtual_machines.*.memory_mb
                label: RAM (MB)
            """);
        var catalog = CapabilityCatalogLoader.Merge("homelab", new[] { view }, Array.Empty<CapabilityDefinition>());

        Assert.True(catalog.Capabilities[0].IsView);
        Assert.Equal("CPU", catalog.Capabilities[0].Signals[0].Label);
    }

    [Fact]
    public void A_per_item_scalar_path_with_rules_is_rejected()
    {
        var yaml = """
            capability: bad
            signals:
              - path: proxmox.virtual_machines.*.cores
                rules:
                  - when: 4
                    actions: [ { key: k, executor: http, http: { url: "https://ops.test/k" } } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("rule-less view signal", ex.Message);
    }

    [Fact]
    public void LoadCapability_parses_container_executor_config()
    {
        var yaml = """
            capability: vms
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: run_ansible_profile
                        executor: container
                        container:
                          image: "homelab/toolbox:latest"
                          command: ["ansible-profile"]
            """;
        var capability = CapabilityCatalogLoader.LoadCapability(yaml);
        var action = capability.Signals[0].Rules[0].Actions[0];

        Assert.Equal(ExecutorKind.Container, action.ExecutorKind);
        Assert.NotNull(action.Container);
        Assert.Equal("homelab/toolbox:latest", action.Container!.Image);
        Assert.Equal(new[] { "ansible-profile" }, action.Container.Command);
    }

    [Fact]
    public void LoadCapability_rejects_container_executor_without_a_container_block()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k, executor: container } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("no 'container' block", ex.Message);
    }

    [Fact]
    public void LoadCapability_rejects_a_container_block_without_container_executor()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: k
                        executor: http
                        http: { url: "https://ops.test/r" }
                        container:
                          image: "img"
                          command: ["run"]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("declares a 'container' block but 'executor' is not 'container'", ex.Message);
    }

    [Fact]
    public void LoadCapability_rejects_the_old_docker_executor_name()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k, executor: docker } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("unknown executor 'docker'", ex.Message);
    }

    [Fact]
    public void LoadCapability_parses_additional_build_contexts_and_keeps_them_out_of_the_json_when_absent()
    {
        var yaml = """
            capability: c
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: k
                        executor: container
                        container:
                          build:
                            context: playbooks/tf
                            additional_contexts: { base: ./playbooks/_base/ }
            """;
        var build = CapabilityCatalogLoader.LoadCapability(yaml).Signals[0].Rules[0].Actions[0].Container!.Build!;
        Assert.Equal(new Dictionary<string, string> { ["base"] = "playbooks/_base" }, build.AdditionalContexts);
        Assert.Contains("\"AdditionalContexts\"", ExecutorConfigJson.Serialize(new ContainerExecutorConfig(null, [], Build: build)));
        Assert.DoesNotContain("AdditionalContexts",
            ExecutorConfigJson.Serialize(new ContainerExecutorConfig(null, [], Build: build with { AdditionalContexts = null })));
    }

    [Theory]
    [InlineData("{ base: ../elsewhere }", "relative path inside")]
    [InlineData("{ Base: playbooks/_base }", "must be lowercase")]
    [InlineData("{ base: \"\" }", "names no folder")]
    public void LoadCapability_rejects_bad_additional_build_contexts(string contexts, string expected)
    {
        var yaml = $$"""
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: k
                        executor: container
                        container:
                          build: { context: playbooks/tf, additional_contexts: {{contexts}} }
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains(expected, ex.Message);
    }

    private const string DefaultsKindYaml = """
        name: "Homelab"
        defaults:
          inputs:
            pass_pat: { secret: PROTON_PASS_PAT }
        """;

    [Fact]
    public void Kind_default_inputs_are_appended_unless_the_action_names_them()
    {
        var yaml = """
            capability: c
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: plain
                        executor: container
                        container: { image: "img" }
                        inputs:
                          host: { from: item.ip }
                      - key: own_token
                        executor: container
                        container: { image: "img" }
                        inputs:
                          pass_pat: { secret: OTHER_PAT }
                      - key: opted_out
                        executor: container
                        container: { image: "img" }
                        inputs:
                          pass_pat: ~
            """;
        var defaults = CapabilityCatalogLoader.LoadKindDefaults(DefaultsKindYaml);
        var actions = CapabilityCatalogLoader.LoadCapability(yaml, defaults: defaults).Signals[0].Rules[0].Actions;

        var plain = actions.Single(a => a.Key == "plain").Inputs;
        Assert.Equal(new[] { "host", "pass_pat" }, plain.Select(i => i.Name));
        Assert.Equal(RuleInputKind.Secret, plain[1].Kind);
        Assert.Equal("PROTON_PASS_PAT", plain[1].Value);

        Assert.Equal("OTHER_PAT", Assert.Single(actions.Single(a => a.Key == "own_token").Inputs).Value);
        Assert.Empty(actions.Single(a => a.Key == "opted_out").Inputs);
    }

    [Fact]
    public void Kind_default_prompt_on_an_auto_action_is_rejected()
    {
        var yaml = """
            capability: c
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: k
                        policy: AUTO
                        executor: container
                        container: { image: "img" }
            """;
        var defaults = CapabilityCatalogLoader.LoadKindDefaults("""
            defaults:
              inputs:
                reason: { prompt: "Why?" }
            """);
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml, defaults: defaults));
        Assert.Contains("AUTO but declares prompt inputs", ex.Message);
    }

    [Fact]
    public void Kind_manifest_without_defaults_has_none_and_a_broken_one_throws()
    {
        Assert.Empty(CapabilityCatalogLoader.LoadKindDefaults("name: \"Homelab\"").Inputs);
        Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadKindDefaults("defaults: [unclosed"));
    }

    [Fact]
    public void LoadCapability_rejects_an_unknown_executor()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k, executor: nonsense } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("unknown executor", ex.Message);
    }

    // --- secret inputs --------------------------------------------------------------------

    [Fact]
    public void LoadCapability_parses_secret_inputs()
    {
        var yaml = """
            capability: vms
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions:
                      - key: k
                        executor: http
                        http: { url: "https://ops.test/r" }
                        inputs:
                          registry_token: { secret: "Homelab/environments/REGISTRY_PULL_TOKEN" }
            """;
        var capability = CapabilityCatalogLoader.LoadCapability(yaml);
        var action = capability.Signals[0].Rules[0].Actions[0];

        Assert.Contains(action.Inputs, i =>
            i.Name == "registry_token" && i.Kind == RuleInputKind.Secret && i.Value == "Homelab/environments/REGISTRY_PULL_TOKEN");
    }

    [Fact]
    public void LoadCapability_allows_auto_actions_with_secret_inputs()
    {
        // Unlike prompt inputs, a secret needs no human to answer it — AUTO + secret is fine.
        var yaml = """
            capability: ok
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: k
                        executor: http
                        http: { url: "https://ops.test/r" }
                        policy: AUTO
                        inputs:
                          token: { secret: "Homelab/environments/TOKEN" }
            """;
        var capability = CapabilityCatalogLoader.LoadCapability(yaml);
        Assert.Equal(ActionPolicy.AUTO, capability.Signals[0].Rules[0].Actions[0].Policy);
    }

    // --- container image / build--------------------------------------------------------------

    private static string ContainerYaml(string containerBlock) => $$"""
        capability: probes
        signals:
          - path: checks
            kind: keyed_collection
            rules:
              - on: add
                actions:
                  - key: probe
                    executor: container
                    container:
        {{containerBlock}}
        """;

    [Fact]
    public void LoadCapability_parses_a_container_build_playbook()
    {
        var capability = CapabilityCatalogLoader.LoadCapability(ContainerYaml("""
                          build:
                            context: ./playbooks//http-probe/
                            dockerfile: docker/Dockerfile
                            target: runtime
                            args:
                              ZETA: "2"
                              ALPINE_VERSION: "3.20"
                          entrypoint: ["/bin/sh", "-c"]
                          command: ["echo hi"]
            """));
        var docker = capability.Signals[0].Rules[0].Actions[0].Container!;

        Assert.Null(docker.Image);
        Assert.Equal(new[] { "/bin/sh", "-c" }, docker.Entrypoint);
        Assert.Equal(new[] { "echo hi" }, docker.Command);
        var build = docker.Build!;
        Assert.Equal("playbooks/http-probe", build.Context);
        Assert.Equal("docker/Dockerfile", build.Dockerfile);
        Assert.Equal("runtime", build.Target);
        Assert.Equal(new[] { "ALPINE_VERSION", "ZETA" }, build.Args!.Keys);
        Assert.Null(build.Fingerprint);
    }

    [Fact]
    public void LoadCapability_allows_an_image_without_command()
    {
        var capability = CapabilityCatalogLoader.LoadCapability(ContainerYaml("""
                          image: "alpine:3.20"
            """));
        var docker = capability.Signals[0].Rules[0].Actions[0].Container!;

        Assert.Equal("alpine:3.20", docker.Image);
        Assert.Empty(docker.Command);
        Assert.Null(docker.Entrypoint);
        Assert.Null(docker.Build);
    }

    [Theory]
    [InlineData("""
                          image: "alpine:3.20"
                          build:
                            context: playbooks/x
        """)]
    [InlineData("""
                          command: ["echo"]
        """)]
    public void LoadCapability_requires_exactly_one_of_image_or_build(string block)
    {
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(ContainerYaml(block)));
        Assert.Contains("exactly one of 'image'", ex.Message);
    }

    [Theory]
    [InlineData("../acme/playbooks/x")]
    [InlineData("playbooks/../../x")]
    [InlineData("/etc")]
    public void LoadCapability_rejects_build_contexts_outside_the_kind_folder(string context)
    {
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(ContainerYaml($$"""
                          build:
                            context: "{{context}}"
            """)));
        Assert.Contains("relative path", ex.Message);
    }

    [Fact]
    public void LoadCapability_rejects_a_build_without_context()
    {
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(ContainerYaml("""
                          build:
                            target: runtime
            """)));
        Assert.Contains("'context'", ex.Message);
    }

    // --- nested signals, exclude, env -----------------------------------------

    [Theory]
    [InlineData("*.containers")]
    [InlineData("proxmox.vms.*")]
    [InlineData("proxmox.*.vms.*.containers")]
    public void LoadCapability_rejects_misplaced_wildcards(string path)
    {
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability($$"""
            capability: c
            signals:
              - path: "{{path}}"
                kind: keyed_collection
            """));
        Assert.Contains("'*' segment", ex.Message);
    }

    [Fact]
    public void LoadCapability_parses_nested_paths_exclude_and_env()
    {
        var capability = CapabilityCatalogLoader.LoadCapability("""
            capability: c
            signals:
              - path: proxmox.vms.*.containers
                kind: keyed_collection
                exclude: [secrets]
                rules:
                  - on: add
                    actions:
                      - key: deploy
                        executor: container
                        container:
                          image: "alpine:3.20"
                          env: { PROTON_PASS_KEY_PROVIDER: fs }
            """);
        var signal = capability.Signals[0];
        Assert.True(signal.IsNested);
        Assert.Equal("proxmox.vms", signal.ParentPath);
        Assert.Equal("containers", signal.ChildPath);
        Assert.Equal(new[] { "secrets" }, signal.Exclude);

        var docker = signal.Rules[0].Actions[0].Container!;
        Assert.Equal("fs", docker.Env!["PROTON_PASS_KEY_PROVIDER"]);
    }

    [Theory]
    [InlineData("env: { LODGE_PARAM_X: y }", "LODGE_")]
    public void LoadCapability_rejects_reserved_env(string line, string expected)
    {
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(ContainerYaml($$"""
                          image: "alpine:3.20"
                          {{line}}
            """)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void LoadCapability_parses_resources_timeout_security_and_network()
    {
        var container = CapabilityCatalogLoader.LoadCapability(ContainerYaml("""
                          image: "alpine:3.20"
                          resources: { memory: 512M, cpus: 1.5, pids: 100 }
                          timeout_seconds: 900
                          network: internal
                          security:
                            cap_add: [net_admin, CHOWN, NET_ADMIN]
                            no_new_privileges: false
                            read_only_rootfs: false
                            user: "1000:1000"
                            tmpfs: [/var/cache/, /run]
            """)).Signals[0].Rules[0].Actions[0].Container!;

        Assert.Equal(new ContainerResources("512m", 1.5, 100), container.Resources);
        Assert.Equal(900, container.TimeoutSeconds);
        Assert.Equal("internal", container.Network);
        Assert.Equal(new[] { "CHOWN", "NET_ADMIN" }, container.Security!.CapAdd);
        Assert.False(container.Security.NoNewPrivileges);
        Assert.False(container.Security.ReadOnlyRootfs);
        Assert.Equal("1000:1000", container.Security.User);
        Assert.Equal(new[] { "/run", "/var/cache" }, container.Security.Tmpfs);
    }

    [Fact]
    public void An_action_without_the_hardening_fields_keeps_its_exact_snapshot_encoding()
    {
        var container = CapabilityCatalogLoader.LoadCapability(ContainerYaml("""
                          image: "alpine:3.20"
            """)).Signals[0].Rules[0].Actions[0].Container!;

        Assert.Equal("{\"Image\":\"alpine:3.20\",\"Command\":[],\"Entrypoint\":null,\"Build\":null,\"Env\":null}",
            ExecutorConfigJson.Serialize(container));
    }

    [Fact]
    public void A_hardening_field_is_part_of_the_snapshot_so_changing_it_requeues()
    {
        string Json(string extra) => ExecutorConfigJson.Serialize(CapabilityCatalogLoader.LoadCapability(ContainerYaml($$"""
                          image: "alpine:3.20"
                          {{extra}}
            """)).Signals[0].Rules[0].Actions[0].Container)!;

        Assert.NotEqual(Json(""), Json("timeout_seconds: 60"));
        Assert.NotEqual(Json("timeout_seconds: 60"), Json("timeout_seconds: 61"));
        Assert.NotEqual(Json(""), Json("security: { read_only_rootfs: false }"));
        Assert.NotEqual(Json(""), Json("network: none"));
        Assert.NotEqual(Json(""), Json("resources: { pids: 10 }"));
    }

    [Theory]
    [InlineData("resources: { memory: lots }", "container.resources.memory")]
    [InlineData("resources: { memory: 0m }", "container.resources.memory")]
    [InlineData("resources: { cpus: 0 }", "container.resources.cpus")]
    [InlineData("resources: { pids: 0 }", "container.resources.pids")]
    [InlineData("timeout_seconds: 0", "container.timeout_seconds")]
    [InlineData("timeout_seconds: 90000", "container.timeout_seconds")]
    [InlineData("network: \"host net\"", "container.network")]
    [InlineData("network: Host", "container.network")]
    [InlineData("security: { cap_add: [\"net admin\"] }", "cap_add")]
    [InlineData("security: { user: \"root; id\" }", "container.security.user")]
    [InlineData("security: { tmpfs: [relative] }", "container.security.tmpfs")]
    [InlineData("security: { tmpfs: [/a/../etc] }", "container.security.tmpfs")]
    [InlineData("security: { tmpfs: [\"/a:ro,exec\"] }", "container.security.tmpfs")]
    public void LoadCapability_rejects_invalid_hardening_fields(string line, string expected)
    {
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(ContainerYaml($$"""
                          image: "alpine:3.20"
                          {{line}}
            """)));
        Assert.Contains(expected, ex.Message);
    }
}
