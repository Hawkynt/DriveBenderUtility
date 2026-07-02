using DivisonM.Vfs;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class PoolPathsTests {

  [Test]
  [Category("HappyPath")]
  public void Normalize_GivenBackslashPath_WhenNormalized_ThenUsesForwardSlashes()
    => PoolPaths.Normalize(@"Documents\Sub\file.txt").Should().Be("Documents/Sub/file.txt");

  [Test]
  [Category("EdgeCase")]
  public void Normalize_GivenLeadingAndTrailingSlashes_WhenNormalized_ThenTrimmed()
    => PoolPaths.Normalize("/Documents/file.txt/").Should().Be("Documents/file.txt");

  [Test]
  [Category("EdgeCase")]
  public void Normalize_GivenEmptyPath_WhenNormalized_ThenStaysEmpty()
    => PoolPaths.Normalize("").Should().Be("");

  [Test]
  [Category("Exception")]
  public void Normalize_GivenTraversalSegment_WhenNormalized_ThenRejected() {
    var act = () => PoolPaths.Normalize("Documents/../secret.txt");
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.InvalidArgument);
  }

  [Test]
  [Category("HappyPath")]
  public void ToPhysical_GivenNestedFileAsShadow_WhenMapped_ThenShadowFolderIsInParent()
    => PoolPaths.ToPhysical("Documents/report.docx", true)
      .Should().Be("Documents/FOLDER.DUPLICATE.$DRIVEBENDER/report.docx");

  [Test]
  [Category("EdgeCase")]
  public void ToPhysical_GivenRootLevelFileAsShadow_WhenMapped_ThenShadowFolderIsAtRoot()
    => PoolPaths.ToPhysical("report.docx", true)
      .Should().Be("FOLDER.DUPLICATE.$DRIVEBENDER/report.docx");

  [Test]
  [Category("HappyPath")]
  public void ToPhysical_GivenPrimary_WhenMapped_ThenPathUnchanged()
    => PoolPaths.ToPhysical("Documents/report.docx", false).Should().Be("Documents/report.docx");

  [TestCase("FOLDER.DUPLICATE.$DRIVEBENDER")]
  [TestCase("folder.duplicate.$drivebender")]
  [TestCase(".drivebenderutility")]
  [TestCase("movie.mkv.TEMP.$DRIVEBENDER")]
  [TestCase("pool info.MP.$DRIVEBENDER")]
  [Category("HappyPath")]
  public void IsHiddenName_GivenSidecarNames_WhenChecked_ThenHidden(string name)
    => PoolPaths.IsHiddenName(name).Should().BeTrue("'{0}' must never appear in the mounted namespace (FR-HIDE)", name);

  [TestCase("report.docx")]
  [TestCase("TEMPLATE.docx")]
  [TestCase("drivebenderutility")]
  [Category("EdgeCase")]
  public void IsHiddenName_GivenOrdinaryNames_WhenChecked_ThenVisible(string name)
    => PoolPaths.IsHiddenName(name).Should().BeFalse();

  [Test]
  [Category("HappyPath")]
  public void GetParentAndName_GivenNestedPath_WhenSplit_ThenCorrect() {
    PoolPaths.GetParent("a/b/c.txt").Should().Be("a/b");
    PoolPaths.GetName("a/b/c.txt").Should().Be("c.txt");
    PoolPaths.GetParent("c.txt").Should().Be("");
    PoolPaths.GetName("c.txt").Should().Be("c.txt");
  }

}
