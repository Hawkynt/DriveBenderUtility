using DivisonM.App.Localization;
using DivisonM.App.ViewModels;
using DivisonM.Backends;
using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.App.Tests;

/// <summary>Headless ViewModel tests (TST-UI): validation parity with the CLI, i18n completeness, activity binding.</summary>
[TestFixture]
[Category("Unit")]
public class LocalizationTests {

  [Test]
  [Category("HappyPath")]
  public void Shipped_GivenEnAndDe_WhenLoaded_ThenBothPresent()
    => Localizer.Instance.AvailableLanguages.Should().BeEquivalentTo(["en", "de"]);

  [Test]
  [Category("HappyPath")]
  public void Resources_GivenGermanSet_WhenComparedToEnglish_ThenNoMissingKeys() {
    var english = new HashSet<string>(Localizer.Instance.KeysOf("en"));
    var german = new HashSet<string>(Localizer.Instance.KeysOf("de"));

    english.Should().NotBeEmpty();
    german.Except(english).Should().BeEmpty("German must not define keys English lacks");
    english.Except(german).Should().BeEmpty("every English key must have a German translation (FR-I18N completeness)");
  }

  [Test]
  [Category("EdgeCase")]
  public void Get_GivenUnknownKey_WhenResolved_ThenFallsBackToKey() {
    var localizer = new Localizer();
    localizer.Get("nonexistent.key").Should().Be("nonexistent.key");
  }

  [Test]
  [Category("HappyPath")]
  public void CurrentLanguage_GivenGerman_WhenSet_ThenGermanStringsReturned() {
    var localizer = new Localizer { CurrentLanguage = "de" };
    localizer.Get("tab.dashboard").Should().Be("Übersicht");
    localizer.CurrentLanguage = "en";
    localizer.Get("tab.dashboard").Should().Be("Dashboard");
  }

  [Test]
  [Category("EdgeCase")]
  public void EnumKindKeys_GivenEveryMemberKind_WhenLookedUp_ThenTranslatedInBothLanguages() {
    foreach (var kind in CreatePoolViewModel.Kinds) {
      Localizer.Instance.KeysOf("en").Should().Contain(kind.DisplayKey);
      Localizer.Instance.KeysOf("de").Should().Contain(kind.DisplayKey);
    }
  }

}

[TestFixture]
[Category("Unit")]
public class TuningViewModelTests {

  [Test]
  [Category("HappyPath")]
  public void Validate_GivenDefaults_WhenChecked_ThenValid() {
    var vm = new TuningViewModel();
    vm.IsValid.Should().BeTrue();
    vm.ApplyCommand.CanExecute(null).Should().BeTrue();
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenMinCopiesAboveDuplication_WhenChecked_ThenRejectedLikeTheCli() {
    var vm = new TuningViewModel { Duplication = 2, MinCopiesBeforeAck = 3 };

    vm.IsValid.Should().BeFalse("the GUI rejects the exact values the CLI rejects (CFG-VALIDATE parity)");
    vm.ApplyCommand.CanExecute(null).Should().BeFalse();
    vm.ValidationMessage.Should().Contain("duplication level");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenVolatileAckWithoutPerformance_WhenChecked_ThenRejected() {
    var vm = new TuningViewModel { WritePolicy = WritePolicy.WriteBack, Duplication = 1, MinCopiesBeforeAck = 1, AcceptVolatileAck = true };
    vm.IsValid.Should().BeFalse("acceptVolatileAck requires the performance policy (SAFE-RAM)");
  }

  [Test]
  [Category("HappyPath")]
  public void ShowVolatileWarning_GivenPerformancePlusVolatileAck_WhenArmed_ThenWarningSurfaced() {
    var vm = new TuningViewModel { WritePolicy = WritePolicy.Performance, Duplication = 1, MinCopiesBeforeAck = 1, AcceptVolatileAck = true };
    vm.ShowVolatileWarning.Should().BeTrue("the GUI must echo the volatility warning (§6.13, SAFE-RAM)");
    vm.VolatileWarning.Should().NotBeEmpty();
    vm.IsValid.Should().BeTrue();
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenBadCacheSize_WhenChecked_ThenRejectedWithMessage() {
    var vm = new TuningViewModel { CacheSize = "not-a-size" };
    vm.IsValid.Should().BeFalse();
    vm.ValidationMessage.Should().NotBeEmpty();
  }

  [Test]
  [Category("HappyPath")]
  public void EvictionPolicies_GivenExposedList_WhenInspected_ThenAllTenPresent()
    => TuningViewModel.EvictionPolicies.Should().HaveCount(10, "every FR-EVICT policy is selectable in the GUI");

}

[TestFixture]
[Category("Unit")]
public class CreatePoolViewModelTests {

  private FakeHostEnvironment _host = null!;
  private PoolLifecycle _lifecycle = null!;

  [SetUp]
  public void SetUp() {
    this._host = new();
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._host.AddVolume(@"B:\", "PHYS-B");
    this._host.AddDirectory(@"B:\pool");
    this._lifecycle = new(this._host, new ManifestStore(this._host));
  }

  [Test]
  [Category("HappyPath")]
  public void AddMember_GivenLocalAndRemote_WhenAdded_ThenListed() {
    var vm = new CreatePoolViewModel(this._lifecycle) { PoolName = "P" };
    vm.SelectedKind = CreatePoolViewModel.Kinds.First(k => k.Scheme == "file");
    vm.MemberLocation = @"A:\";
    vm.AddMember();
    vm.SelectedKind = CreatePoolViewModel.Kinds.First(k => k.Scheme == "sftp");
    vm.MemberLocation = "sftp://user@host/pool";
    vm.AddMember();

    vm.Members.Should().HaveCount(2);
    vm.CreateCommand.CanExecute(null).Should().BeTrue();
  }

  [Test]
  [Category("HappyPath")]
  public void Create_GivenMixedMembers_WhenCreated_ThenManifestHasSchemesAndNetworkFlags() {
    var vm = new CreatePoolViewModel(this._lifecycle) { PoolName = "MixedPool" };
    vm.SelectedKind = CreatePoolViewModel.Kinds.First(k => k.Scheme == "file");
    vm.MemberLocation = @"B:\pool";
    vm.AddMember();
    vm.SelectedKind = CreatePoolViewModel.Kinds.First(k => k.Scheme == "s3");
    vm.MemberLocation = "s3://bucket/prefix";
    vm.CredentialReference = "my-s3";
    vm.AddMember();

    var manifest = vm.Create();

    manifest.Members.Should().HaveCount(2);
    var s3 = manifest.Members.Single(m => m.Path.StartsWith("s3://"));
    s3.Scheme.Should().Be("s3");
    s3.Network.Should().BeTrue();
    s3.Credential.Should().Be("cred-ref:my-s3");
    manifest.Members.Single(m => m.Path == @"B:\pool").Scheme.Should().BeNull("local members carry no scheme");
  }

  [Test]
  [Category("EdgeCase")]
  public void IsLocationBrowsable_GivenKind_WhenLocal_ThenTrueElseFalse() {
    var vm = new CreatePoolViewModel(this._lifecycle);
    vm.SelectedKind = CreatePoolViewModel.Kinds.First(k => k.Scheme == "file");
    vm.IsLocationBrowsable.Should().BeTrue();
    vm.SelectedKind = CreatePoolViewModel.Kinds.First(k => k.Scheme == "ftp");
    vm.IsLocationBrowsable.Should().BeFalse();
  }

  [Test]
  [Category("EdgeCase")]
  public void AddMember_GivenDuplicateLocation_WhenAdded_ThenIgnored() {
    var vm = new CreatePoolViewModel(this._lifecycle);
    vm.SelectedKind = CreatePoolViewModel.Kinds.First(k => k.Scheme == "file");
    vm.MemberLocation = @"A:\";
    vm.AddMember();
    vm.MemberLocation = @"A:\";
    vm.AddMember();
    vm.Members.Should().HaveCount(1);
  }

  [Test]
  [Category("HappyPath")]
  public void Kinds_GivenExposedList_WhenInspected_ThenEveryPrdMemberKindPresent() {
    var schemes = CreatePoolViewModel.Kinds.Select(k => k.Scheme).ToArray();
    schemes.Should().Contain(["file", "unc", "ftp", "ftps", "sftp", "webdav", "webdavs", "s3", "azblob", "azfile", "dropbox", "onedrive", "gdrive", "gcs"]);
  }

}

[TestFixture]
[Category("Unit")]
public class DashboardAndCredentialViewModelTests {

  [Test]
  [Category("HappyPath")]
  public void Dashboard_GivenProviderWithPools_WhenRefreshed_ThenRowsBound() {
    var host = new FakeHostEnvironment();
    host.AddVolume(@"A:\", "PHYS-A");
    host.AddDirectory(@"A:\p");
    var store = new ManifestStore(host);
    new PoolLifecycle(host, store).Create("DashPool", [new(@"A:\p")], force: true);
    var provider = new PoolProvider(host, store, [new JsonManifestSource(store)]);

    var vm = new DashboardViewModel(provider);

    vm.Pools.Should().ContainSingle().Which.Name.Should().Be("DashPool");
  }

  [Test]
  [Category("HappyPath")]
  public void Credentials_GivenSecret_WhenStored_ThenPlaintextClearedAndResolvable() {
    var host = new FakeHostEnvironment();
    var store = new CredentialStore(host, useOsStore: false);
    var vm = new CredentialsViewModel(store) { Name = "srv", User = "u", Secret = "topsecret" };

    vm.Store();

    vm.Secret.Should().BeEmpty("the ViewModel drops the plaintext after storing it (SEC-CRED)");
    store.Resolve("cred-ref:srv")!.Secret.Should().Be("topsecret");
  }

}

[TestFixture]
[Category("Unit")]
public class ActivityViewModelTests {

  [Test]
  [Category("HappyPath")]
  public void Activity_GivenFeedEvents_WhenPublished_ThenBoundNewestFirst() {
    var feed = new ActivityFeed(maxEventsPerSecond: 1000);
    var vm = new ActivityViewModel(feed);

    feed.Publish(ActivityKind.Write, "a.bin", 10);
    feed.Publish(ActivityKind.Read, "b.bin", 20);

    vm.Events.Should().HaveCount(2);
    vm.Events[0].Path.Should().Be("b.bin", "newest first");
  }

  [Test]
  [Category("EdgeCase")]
  public void Activity_GivenFlood_WhenBounded_ThenBufferNeverExceedsCap() {
    var feed = new ActivityFeed(maxEventsPerSecond: 100000);
    var vm = new ActivityViewModel(feed, maxRows: 50);

    for (var i = 0; i < 500; ++i)
      feed.Publish(ActivityKind.Read, $"f{i}", 1);

    vm.Events.Count.Should().BeLessThanOrEqualTo(50, "the view caps its history so it never grows unbounded (NFR-UI-LIVE)");
  }

  [Test]
  [Category("EdgeCase")]
  public void Activity_GivenPaused_WhenEventsArrive_ThenIgnored() {
    var feed = new ActivityFeed(maxEventsPerSecond: 1000);
    var vm = new ActivityViewModel(feed) { Paused = true };

    feed.Publish(ActivityKind.Write, "x", 1);

    vm.Events.Should().BeEmpty();
  }

}
