using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>The retain-metadata shadow namespace is LRU-bounded so it cannot grow without limit on a huge pool.</summary>
[TestFixture]
[Category("Unit")]
public class ShadowNamespaceTests {

  private static NamespaceNode _File(long len) => new(NodeKind.File, len, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

  [Test]
  [Category("EdgeCase")]
  public void Record_GivenMoreThanTheCap_WhenRecorded_ThenOldestEvictedNewestKept() {
    var ns = new ShadowNamespace(maxEntries: 3);
    ns.Record("a.txt", _File(1));
    ns.Record("b.txt", _File(2));
    ns.Record("c.txt", _File(3));
    ns.Record("d.txt", _File(4)); // evicts a
    ns.Record("e.txt", _File(5)); // evicts b

    ns.Count.Should().Be(3, "the namespace is bounded to its cap");
    ns.Get("a.txt").Should().BeNull("the oldest-recorded path was evicted");
    ns.Get("b.txt").Should().BeNull();
    ns.Get("c.txt").Should().NotBeNull("the most recent entries are retained");
    ns.Get("e.txt")!.Length.Should().Be(5);
  }

  [Test]
  [Category("EdgeCase")]
  public void Record_GivenReRecordOfExisting_WhenOverCap_ThenReRecordedPathSurvives() {
    var ns = new ShadowNamespace(maxEntries: 3);
    ns.Record("a.txt", _File(1));
    ns.Record("b.txt", _File(2));
    ns.Record("c.txt", _File(3));
    ns.Record("a.txt", _File(11)); // touch 'a' → now most-recently-used
    ns.Record("d.txt", _File(4));  // evicts the LRU, which is now 'b', not 'a'

    ns.Get("a.txt")!.Length.Should().Be(11, "re-recording keeps a hot path alive across eviction");
    ns.Get("b.txt").Should().BeNull("the least-recently-used path was evicted");
    ns.Count.Should().Be(3);
  }

  [Test]
  [Category("HappyPath")]
  public void RemoveAndChildren_GivenSubtree_WhenRemoved_ThenSubtreeGone() {
    var ns = new ShadowNamespace(maxEntries: 100);
    ns.Record("dir", new(NodeKind.Directory, 0, DateTime.MinValue));
    ns.Record("dir/x.txt", _File(1));
    ns.Record("dir/y.txt", _File(2));
    ns.Record("other.txt", _File(3));

    ns.Children("dir").Should().HaveCount(2);
    ns.Remove("dir");

    ns.Get("dir/x.txt").Should().BeNull("removing a directory removes its subtree");
    ns.Get("other.txt").Should().NotBeNull("unrelated paths are untouched");
  }

  // The index behind Children/Remove/Rename. These used to scan EVERY remembered path — every
  // listing, and every single-file delete or rename — so their cost grew with the pool rather than
  // with the folder. Profiled during a tree copy, listings were a large share of the driver's time.
  // What the index must preserve is below, including the awkward shapes a prefix scan got for free.

  private static NamespaceNode _Dir() => new(NodeKind.Directory, 0, DateTime.MinValue);

  [Test]
  [Category("HappyPath")]
  public void Children_GivenNestedPaths_WhenListed_ThenOnlyImmediateChildrenAreReturned() {
    var ns = new ShadowNamespace(maxEntries: 100);
    ns.Record("a", _Dir());
    ns.Record("a/x.txt", _File(1));
    ns.Record("a/b", _Dir());
    ns.Record("a/b/deep.txt", _File(2));
    ns.Record("ab.txt", _File(3)); // shares the prefix "a" as a STRING, is not a child of "a"

    ns.Children("a").Select(c => c.Name).Should().BeEquivalentTo(["x.txt", "b"]);
    ns.Children("").Select(c => c.Name).Should().BeEquivalentTo(["a", "ab.txt"], "the root lists its own children");
  }

  [Test]
  [Category("EdgeCase")]
  public void Remove_GivenADescendantUnderAFolderThatWasNeverRecorded_WhenTheAncestorIsRemoved_ThenTheDescendantGoesToo() {
    // only the leaf was ever surfaced — its parent folder has no node of its own
    var ns = new ShadowNamespace(maxEntries: 100);
    ns.Record("top", _Dir());
    ns.Record("top/unrecorded/leaf.txt", _File(1));

    ns.Remove("top");

    ns.Get("top/unrecorded/leaf.txt").Should().BeNull("a removed directory takes its whole subtree, recorded ancestors or not");
    ns.Count.Should().Be(0);
  }

  [Test]
  [Category("HappyPath")]
  public void Rename_GivenASubtree_WhenTheFolderIsRenamed_ThenEveryDescendantMovesAndListsUnderTheNewName() {
    var ns = new ShadowNamespace(maxEntries: 100);
    ns.Record("old", _Dir());
    ns.Record("old/a.txt", _File(1));
    ns.Record("old/sub/b.txt", _File(2));

    ns.Rename("old", "new");

    ns.Get("old/a.txt").Should().BeNull();
    ns.Get("new/a.txt")!.Length.Should().Be(1);
    ns.Get("new/sub/b.txt")!.Length.Should().Be(2);
    ns.Children("new").Select(c => c.Name).Should().Contain("a.txt");
    ns.Children("old").Should().BeEmpty("nothing may still list under the old name");
  }

  [Test]
  [Category("EdgeCase")]
  public void Children_GivenAnEntryWasEvicted_WhenListed_ThenItIsNoLongerAChild() {
    var ns = new ShadowNamespace(maxEntries: 2);
    ns.Record("d/1.txt", _File(1));
    ns.Record("d/2.txt", _File(2));
    ns.Record("d/3.txt", _File(3)); // evicts d/1.txt

    ns.Children("d").Select(c => c.Name).Should().BeEquivalentTo(["2.txt", "3.txt"], "the index must forget what eviction forgets");
  }

  [Test]
  [Category("EdgeCase")]
  public void Remove_GivenAFile_WhenRemoved_ThenSiblingsAndAPrefixSharingNeighbourSurvive() {
    var ns = new ShadowNamespace(maxEntries: 100);
    ns.Record("f.txt", _File(1));
    ns.Record("f.txt.bak", _File(2));
    ns.Record("dir/f.txt", _File(3));

    ns.Remove("f.txt");

    ns.Get("f.txt").Should().BeNull();
    ns.Get("f.txt.bak").Should().NotBeNull("a name that merely starts with the removed one is not its child");
    ns.Get("dir/f.txt").Should().NotBeNull();
  }

  [Test]
  [Category("EdgeCase")]
  public void Clear_GivenEntries_WhenCleared_ThenNothingListsAnyMore() {
    var ns = new ShadowNamespace(maxEntries: 100);
    ns.Record("d/x.txt", _File(1));
    ns.Clear();

    ns.Children("d").Should().BeEmpty();
    ns.Count.Should().Be(0);
  }
}
