using DivisonM.Vfs;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class SizeAndDurationSpecTests {

  [TestCase("1024", 1024L)]
  [TestCase("1KiB", 1024L)]
  [TestCase("1MiB", 1048576L)]
  [TestCase("4GiB", 4294967296L)]
  [TestCase("1.5GiB", 1610612736L)]
  [TestCase("1kb", 1000L)]
  [TestCase("2TB", 2000000000000L)]
  [TestCase("0", 0L)]
  [Category("HappyPath")]
  public void ParseBytes_GivenValidSizes_WhenParsed_ThenExactBytes(string text, long expected)
    => SizeSpec.ParseBytes(text).Should().Be(expected);

  [TestCase("")]
  [TestCase("abc")]
  [TestCase("-5MiB")]
  [TestCase("GiB")]
  [TestCase("50%")]
  [Category("Exception")]
  public void ParseBytes_GivenInvalidSizes_WhenParsed_ThenRejected(string text) {
    var act = () => SizeSpec.ParseBytes(text);
    act.Should().Throw<ManifestException>();
  }

  [Test]
  [Category("HappyPath")]
  public void Parse_GivenPercent_WhenResolvedAgainstBaseline_ThenProportional() {
    var spec = SizeSpec.Parse("50%");
    spec.Percent.Should().Be(50);
    spec.ResolveBytes(8_000_000_000).Should().Be(4_000_000_000);
  }

  [TestCase("101%")]
  [TestCase("-1%")]
  [Category("Exception")]
  public void Parse_GivenOutOfRangePercent_WhenParsed_ThenRejected(string text) {
    var act = () => SizeSpec.Parse(text);
    act.Should().Throw<ManifestException>();
  }

  [TestCase("500ms", 0.5)]
  [TestCase("5s", 5.0)]
  [TestCase("2m", 120.0)]
  [TestCase("1h", 3600.0)]
  [TestCase("7d", 604800.0)]
  [Category("HappyPath")]
  public void ParseDuration_GivenValidDurations_WhenParsed_ThenExactSeconds(string text, double seconds)
    => DurationSpec.Parse(text).TotalSeconds.Should().Be(seconds);

  [TestCase("")]
  [TestCase("5")]
  [TestCase("-5s")]
  [TestCase("weekly")]
  [Category("Exception")]
  public void ParseDuration_GivenInvalidDurations_WhenParsed_ThenRejected(string text) {
    var act = () => DurationSpec.Parse(text);
    act.Should().Throw<ManifestException>();
  }

}

[TestFixture]
[Category("Unit")]
public class ConfigResolverTests {

  [Test]
  [Category("HappyPath")]
  public void ResolveEffective_GivenNothing_WhenResolved_ThenTunedDefaultsApply() {
    var config = ConfigResolver.ResolveEffective(null, null);

    config.Write!.Policy.Should().Be(WritePolicy.WriteBack);
    config.Write.MinCopiesBeforeAck.Should().Be(2);
    config.Write.AcceptVolatileAck.Should().BeFalse();
    config.Caches!["global"].Size.Should().Be("4GiB");
    config.Caches["global"].ReadEviction.Should().Be(EvictionPolicy.Arc);
    config.ReadAhead!.MaxWindow.Should().Be("8MiB");
    config.Placement!.Strategy.Should().Be(PlacementStrategy.MostFreeSpace);
    config.Safety!.JournalEnabled.Should().BeTrue();
    config.Trash!.Enabled.Should().BeFalse("deletes stay permanent unless opted in (§6.14)");
  }

  [Test]
  [Category("HappyPath")]
  public void ResolveEffective_GivenGlobalAndPoolLayers_WhenResolved_ThenPoolWinsKeyWise() {
    const string globalJson = """{ "write": { "policy": "write-through", "minCopiesBeforeAck": 3 } }""";
    const string poolJson = """{ "write": { "policy": "deferred" } }""";

    var config = ConfigResolver.ResolveEffective(globalJson, poolJson);

    config.Write!.Policy.Should().Be(WritePolicy.Deferred, "pool overrides global");
    config.Write.MinCopiesBeforeAck.Should().Be(3, "keys the pool omits inherit from global");
    config.Write.DeferWindow.Should().Be("5s", "keys nobody sets inherit the built-in default");
  }

  [Test]
  [Category("HappyPath")]
  public void ResolveForFolder_GivenMatchingGlobOverride_WhenResolved_ThenFolderWins() {
    const string poolJson = """
    {
      "write": { "policy": "write-back" },
      "folders": {
        "Documents/**": { "write": { "policy": "write-through" } }
      }
    }
    """;
    var poolConfig = ConfigResolver.ResolveEffective(null, poolJson);

    var documents = ConfigResolver.ResolveForFolder(poolConfig, "Documents/taxes/2026");
    var other = ConfigResolver.ResolveForFolder(poolConfig, "Movies/film.mkv");

    documents.Write!.Policy.Should().Be(WritePolicy.WriteThrough);
    other.Write!.Policy.Should().Be(WritePolicy.WriteBack);
  }

  [Test]
  [Category("EdgeCase")]
  public void ResolveForFolder_GivenNestedGlobs_WhenResolved_ThenMoreSpecificAppliesLast() {
    const string poolJson = """
    {
      "folders": {
        "Data/**": { "duplication": 2 },
        "Data/Scratch/**": { "duplication": 1, "write": { "policy": "performance", "acceptVolatileAck": true, "minCopiesBeforeAck": 1 } }
      }
    }
    """;
    var poolConfig = ConfigResolver.ResolveEffective(null, poolJson);

    ConfigResolver.ResolveForFolder(poolConfig, "Data/important.db").Duplication.Should().Be(2);
    ConfigResolver.ResolveForFolder(poolConfig, "Data/Scratch/tmp.bin").Duplication.Should().Be(1);
  }

  [TestCase("Documents/**", "Documents/a/b/c.txt", true)]
  [TestCase("Documents/**", "Documents", false)]
  [TestCase("*.iso", "image.iso", true)]
  [TestCase("*.iso", "sub/image.iso", false)]
  [TestCase("a/?/c", "a/b/c", true)]
  [TestCase("a/?/c", "a/bb/c", false)]
  [Category("EdgeCase")]
  public void GlobMatches_GivenPatterns_WhenMatched_ThenCorrect(string glob, string path, bool expected)
    => ConfigResolver.GlobMatches(glob, path).Should().Be(expected);

  [Test]
  [Category("EdgeCase")]
  public void ResolveEffective_GivenUnknownKeys_WhenResolved_ThenPreservedInExtensionData() {
    var config = ConfigResolver.ResolveEffective(null, """{ "futureKnob": 42 }""");
    config.ExtensionData.Should().ContainKey("futureKnob", "unknown keys survive (CFG-SCHEMA)");
  }

}

[TestFixture]
[Category("Unit")]
public class ConfigValidatorTests {

  private static PoolConfig _Config(string poolJson) => ConfigResolver.ResolveEffective(null, poolJson);

  [Test]
  [Category("HappyPath")]
  public void Validate_GivenOutOfBoxDefaults_WhenValidated_ThenAccepted() {
    var act = () => ConfigValidator.Validate(ConfigResolver.ResolveEffective(null, """{ "duplication": 2 }"""), 16L << 30);
    act.Should().NotThrow("zero-config must be valid (CFG-DEFAULT)");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenMinCopiesAboveDuplicationLevel_WhenValidated_ThenRejected() {
    var config = _Config("""{ "duplication": 2, "write": { "minCopiesBeforeAck": 3 } }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*must not exceed the duplication level*");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenMinCopiesBelowOne_WhenValidated_ThenRejected() {
    var config = _Config("""{ "duplication": 1, "write": { "minCopiesBeforeAck": 0 } }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*at least 1*");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenDuplicatedFolderBelowSafetyFloor_WhenValidated_ThenRejected() {
    var config = _Config("""{ "duplication": 3, "write": { "minCopiesBeforeAck": 1 } }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*safety floor*", "CFG-SAFE-FLOOR: floor is min(2, D) for D≥2");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenVolatileAckWithoutPerformancePolicy_WhenValidated_ThenRejected() {
    var config = _Config("""{ "write": { "policy": "write-back", "acceptVolatileAck": true, "minCopiesBeforeAck": 1 }, "duplication": 1 }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*performance*", "SAFE-RAM: volatile ack is a performance-mode-only opt-in");
  }

  [Test]
  [Category("HappyPath")]
  public void Validate_GivenVolatileAckWithPerformancePolicy_WhenValidated_ThenAccepted() {
    var config = _Config("""{ "write": { "policy": "performance", "acceptVolatileAck": true, "minCopiesBeforeAck": 1 }, "duplication": 1 }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().NotThrow();
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenFsyncDurabilityDisabled_WhenValidated_ThenRejected() {
    var config = _Config("""{ "write": { "fsyncIsDurable": false } }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*fsync*", "SAFE-FSYNC cannot be turned off");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenJournalDisabled_WhenValidated_ThenRejected() {
    var config = _Config("""{ "safety": { "journalEnabled": false } }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*journal*", "SAFE-WAL is Must-tier");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenDeferWindowBeyondMaxDefer_WhenValidated_ThenRejected() {
    var config = _Config("""{ "write": { "deferWindow": "60s", "maxDeferSeconds": 30 } }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*deferWindow*");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenInvertedWatermarks_WhenValidated_ThenRejected() {
    var config = _Config("""{ "tiers": { "fast": { "highWatermark": "70%", "lowWatermark": "80%" } } }""");
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*lowWatermark*");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenCacheOverCommit_WhenValidated_ThenRejected() {
    // 12GiB of fixed cache instances against a 16GiB machine with a 50% ceiling (8GiB)
    var config = _Config("""
    {
      "caches": {
        "global": { "size": "4GiB" },
        "media": { "size": "8GiB" }
      }
    }
    """);
    var act = () => ConfigValidator.Validate(config, 16L << 30);
    act.Should().Throw<ConfigValidationException>().WithMessage("*over-commit*", "SAFE-RAM-BUDGET: the ceiling is never over-committed");
  }

  [Test]
  [Category("HappyPath")]
  public void Validate_GivenCachesWithinCeiling_WhenValidated_ThenAccepted() {
    var config = _Config("""{ "caches": { "global": { "size": "4GiB" }, "media": { "size": "2GiB" } } }""");
    var act = () => ConfigValidator.Validate(config, 16L << 30);
    act.Should().NotThrow();
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenSharedFixedSplitNotSummingTo100_WhenValidated_ThenRejected() {
    var config = _Config("""{ "caches": { "global": { "split": { "mode": "shared-fixed", "read": "70%", "write": "40%" } } } }""");
    var act = () => ConfigValidator.Validate(config, 16L << 30);
    act.Should().Throw<ConfigValidationException>().WithMessage("*sum to 100%*");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenSeparateSplitWithoutCaps_WhenValidated_ThenRejected() {
    var config = _Config("""{ "caches": { "global": { "split": { "mode": "separate" } } } }""");
    var act = () => ConfigValidator.Validate(config, 16L << 30);
    act.Should().Throw<ConfigValidationException>().WithMessage("*separate*");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenUnknownCacheReference_WhenValidated_ThenRejected() {
    var config = _Config("""{ "cache": { "use": "nonexistent" } }""");
    var act = () => ConfigValidator.Validate(config, 16L << 30);
    act.Should().Throw<ConfigValidationException>().WithMessage("*unknown cache instance*");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenInvalidFolderOverride_WhenValidated_ThenRejected() {
    var config = _Config("""
    {
      "duplication": 2,
      "folders": { "Scratch/**": { "write": { "acceptVolatileAck": true } } }
    }
    """);
    var act = () => ConfigValidator.Validate(config);
    act.Should().Throw<ConfigValidationException>("folder overrides validate with the same rules (CFG-VALIDATE parity)");
  }

  [Test]
  [Category("Exception")]
  public void ValidateTierAssignments_GivenRoleContradictsTierList_WhenValidated_ThenRejected() {
    var manifest = new PoolManifest {
      PoolId = Guid.NewGuid(),
      Name = "P",
      Members = [new() { MemberId = Guid.NewGuid(), Path = @"A:\", Role = MemberRole.Landing, Label = "SSD" }],
    };
    var config = _Config("""{ "tiers": { "capacity": { "members": ["SSD"] } } }""");

    var act = () => ConfigValidator.ValidateTierAssignments(manifest, config);
    act.Should().Throw<ConfigValidationException>().WithMessage("*must agree*");
  }

  [Test]
  [Category("HappyPath")]
  public void ValidateTierAssignments_GivenAgreeingSources_WhenValidated_ThenAccepted() {
    var manifest = new PoolManifest {
      PoolId = Guid.NewGuid(),
      Name = "P",
      Members = [new() { MemberId = Guid.NewGuid(), Path = @"A:\", Role = MemberRole.Landing, Label = "SSD" }],
    };
    var config = _Config("""{ "tiers": { "fast": { "members": ["SSD"] } } }""");

    var act = () => ConfigValidator.ValidateTierAssignments(manifest, config);
    act.Should().NotThrow();
  }

}
