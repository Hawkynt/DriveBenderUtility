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
}
