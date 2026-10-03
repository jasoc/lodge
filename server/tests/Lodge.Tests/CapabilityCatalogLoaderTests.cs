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
                    runbook: acme-instance-ops/configure-sso
                    label: "Configure SSO"
                    policy: AUTO
                    inputs:
                      instance: { from: instance }
                      mode: { const: "enable" }
                  - key: redeploy
                    runbook: acme-instance-ops/redeploy
                    policy: MANUAL_REQUIRED
                    inputs:
                      instance: { from: instance }
                      environment: { prompt: "Target environment", required: true }
              - when: false
                actions:
                  - key: configure_sso
                    runbook: acme-instance-ops/configure-sso
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
        Assert.Equal("acme-instance-ops/configure-sso", configure.Runbook);
        Assert.Equal("Configure SSO", configure.Label);
        Assert.Equal(ActionPolicy.AUTO, configure.Policy);
        Assert.Contains(configure.Inputs, i => i.Name == "instance" && i.Kind == RuleInputKind.From && i.Value == "instance");
        Assert.Contains(configure.Inputs, i => i.Name == "mode" && i.Kind == RuleInputKind.Const && i.Value == "enable");

        var redeploy = enableRule.Actions[1];
        Assert.Equal("acme-instance-ops/redeploy", redeploy.Label); // label defaults to runbook
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
                    actions: [ { runbook: r } ]
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
                    actions: [ { key: k, runbook: r } ]
            """;
        Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(onForScalar));

        var whenForCollection = """
            capability: bad
            signals:
              - path: vms
                kind: keyed_collection
                rules:
                  - when: true
                    actions: [ { key: k, runbook: r } ]
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
                    actions: [ { key: k, runbook: r } ]
            """;
        Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
    }

    [Fact]
    public void LoadCapability_rejects_same_runbook_under_two_triggers_of_one_signal()
    {
        var yaml = """
            capability: vms
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    actions: [ { key: provision, runbook: acme/provision } ]
                  - on: modify
                    actions: [ { key: update, runbook: acme/provision } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("distinct runbook per trigger", ex.Message);
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
                    actions: [ { key: provision, runbook: acme/provision } ]
                  - on: modify
                    actions: [ { key: provision, runbook: acme/update } ]
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
                      - { key: provision, runbook: acme/provision }
                      - { key: provision, runbook: acme/provision-2 }
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
                        runbook: r
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
                    actions: [ { key: provision, runbook: acme/provision } ]
            """);
        var overrides = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: add
                    item_key: vm-quirky
                    actions: [ { key: apply_quirk_profile, runbook: acme/apply-quirk-profile } ]
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
                    actions: [ { key: provision, runbook: acme/provision } ]
            """);
        var overrides = CapabilityCatalogLoader.LoadCapability("""
            capability: virtual_machines
            signals:
              - path: virtual_machines
                kind: keyed_collection
                rules:
                  - on: delete
                    actions: [ { key: destroy, runbook: acme/destroy } ]
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
                    actions: [ { key: configure, runbook: acme/configure } ]
            """);
        var overrides = CapabilityCatalogLoader.LoadCapability("""
            capability: special
            signals:
              - path: special.flag
                rules:
                  - when: true
                    actions: [ { key: special, runbook: acme/special } ]
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
                    actions: [ { key: provision, runbook: acme/provision } ]
                  - on: modify
                    actions:
                      - key: update
                        runbook: acme/update
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
                        runbook: acme/provision
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
                    actions: [ { key: provision, runbook: acme/provision } ]
                  - on: modify
                    actions:
                      - key: update
                        runbook: acme/update
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
                        runbook: acme/a
                        depends_on: [ "virtual_machines.b" ]
                  - on: modify
                    actions:
                      - key: b
                        runbook: acme/b
                        depends_on: [ "virtual_machines.a" ]
            """);

        var ex = Assert.Throws<CatalogFormatException>(
            () => CapabilityCatalogLoader.Merge("acme", new[] { capability }, Array.Empty<CapabilityDefinition>()));
        Assert.Contains("cycle", ex.Message);
    }

    // --- executor -----------------------------------------------------------------------

    [Fact]
    public void LoadCapability_defaults_executor_to_shell()
    {
        var capability = CapabilityCatalogLoader.LoadCapability(SsoYaml);
        Assert.Equal(ExecutorKind.Shell, capability.Signals[0].Rules[0].Actions[0].ExecutorKind);
        Assert.Null(capability.Signals[0].Rules[0].Actions[0].Docker);
    }

    [Fact]
    public void LoadCapability_parses_docker_executor_config()
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
                        runbook: homelab-ops/ansible-profile
                        executor: docker
                        docker:
                          image: "homelab/toolbox:latest"
                          command: ["ansible-profile"]
            """;
        var capability = CapabilityCatalogLoader.LoadCapability(yaml);
        var action = capability.Signals[0].Rules[0].Actions[0];

        Assert.Equal(ExecutorKind.Docker, action.ExecutorKind);
        Assert.NotNull(action.Docker);
        Assert.Equal("homelab/toolbox:latest", action.Docker!.Image);
        Assert.Equal(new[] { "ansible-profile" }, action.Docker.Command);
    }

    [Fact]
    public void LoadCapability_rejects_docker_executor_without_a_docker_block()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k, runbook: r, executor: docker } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("no 'docker' block", ex.Message);
    }

    [Fact]
    public void LoadCapability_rejects_a_docker_block_without_docker_executor()
    {
        var yaml = """
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions:
                      - key: k
                        runbook: r
                        docker:
                          image: "img"
                          command: ["run"]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("'executor' is not 'docker'", ex.Message);
    }

    [Theory]
    [InlineData("octopus")]
    [InlineData("kubernetes")]
    public void LoadCapability_rejects_reserved_executors(string executor)
    {
        var yaml = $$"""
            capability: bad
            signals:
              - path: features.x
                rules:
                  - when: true
                    actions: [ { key: k, runbook: r, executor: {{executor}} } ]
            """;
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(yaml));
        Assert.Contains("reserved for a future release", ex.Message);
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
                    actions: [ { key: k, runbook: r, executor: nonsense } ]
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
                        runbook: r
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
                        runbook: r
                        policy: AUTO
                        inputs:
                          token: { secret: "Homelab/environments/TOKEN" }
            """;
        var capability = CapabilityCatalogLoader.LoadCapability(yaml);
        Assert.Equal(ActionPolicy.AUTO, capability.Signals[0].Rules[0].Actions[0].Policy);
    }

    // --- docker image / build -----------------------------------------------------------------

    private static string DockerYaml(string dockerBlock) => $$"""
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
        {{dockerBlock}}
        """;

    [Fact]
    public void LoadCapability_parses_a_docker_build_playbook()
    {
        var capability = CapabilityCatalogLoader.LoadCapability(DockerYaml("""
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
        var docker = capability.Signals[0].Rules[0].Actions[0].Docker!;

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
        var capability = CapabilityCatalogLoader.LoadCapability(DockerYaml("""
                          image: "alpine:3.20"
            """));
        var docker = capability.Signals[0].Rules[0].Actions[0].Docker!;

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
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(DockerYaml(block)));
        Assert.Contains("exactly one of 'image'", ex.Message);
    }

    [Theory]
    [InlineData("../acme/playbooks/x")]
    [InlineData("playbooks/../../x")]
    [InlineData("/etc")]
    public void LoadCapability_rejects_build_contexts_outside_the_kind_folder(string context)
    {
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(DockerYaml($$"""
                          build:
                            context: "{{context}}"
            """)));
        Assert.Contains("relative path", ex.Message);
    }

    [Fact]
    public void LoadCapability_rejects_a_build_without_context()
    {
        var ex = Assert.Throws<CatalogFormatException>(() => CapabilityCatalogLoader.LoadCapability(DockerYaml("""
                          build:
                            target: runtime
            """)));
        Assert.Contains("'context'", ex.Message);
    }
}
