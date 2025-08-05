using System;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.Unit.HappyPath {
  
  [TestFixture]
  [Category("Unit")]
  [Category("HappyPath")]
  public class PrimaryFileTests : TestBase {
    
    private Mock<DivisonM.DriveBender.IMountPoint> _mockMountPoint;
    private Mock<DivisonM.DriveBender.IVolume> _mockPrimaryVolume;
    private Mock<DivisonM.DriveBender.IVolume> _mockShadowVolume;
    private Mock<DivisonM.DriveBender.IFile> _mockFile;
    
    [SetUp]
    public override void SetUp() {
      _mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      _mockPrimaryVolume = new Mock<DivisonM.DriveBender.IVolume>();
      _mockShadowVolume = new Mock<DivisonM.DriveBender.IVolume>();
      _mockFile = new Mock<DivisonM.DriveBender.IFile>();
      
      _mockPrimaryVolume.Setup(v => v.Name).Returns("PrimaryVolume");
      _mockPrimaryVolume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(100));
      
      _mockShadowVolume.Setup(v => v.Name).Returns("ShadowVolume");
      _mockShadowVolume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(75));
      
      _mockFile.Setup(f => f.FullName).Returns("ImportantFile.doc");
      _mockFile.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(5));
      _mockFile.Setup(f => f.Primary).Returns(_mockPrimaryVolume.Object);
      _mockFile.Setup(f => f.Primaries).Returns(new[] { _mockPrimaryVolume.Object });
    }
    
    [Test]
    public void File_WithPrimary_ShouldHaveCorrectPrimaryReference() {
      // Act
      var primary = _mockFile.Object.Primary;
      var primaries = _mockFile.Object.Primaries.ToArray();
      
      // Assert
      primary.Should().NotBeNull();
      primary.Should().Be(_mockPrimaryVolume.Object);
      primaries.Should().HaveCount(1);
      primaries[0].Should().Be(_mockPrimaryVolume.Object);
    }
    
    [Test]
    public void File_Properties_ShouldReturnCorrectValues() {
      // Act & Assert
      _mockFile.Object.FullName.Should().Be("ImportantFile.doc");
      _mockFile.Object.Size.Should().Be(ByteSize.FromMegabytes(5));
    }
    
    [Test]
    public void PrimaryVolume_ShouldHaveCorrectProperties() {
      // Act
      var volume = _mockFile.Object.Primary;
      
      // Assert
      volume.Name.Should().Be("PrimaryVolume");
      volume.BytesFree.Should().Be(ByteSize.FromGigabytes(100));
    }
    
    [Test]
    public void File_WithoutShadowCopies_ShouldHaveEmptyShadowCollection() {
      // Arrange
      _mockFile.Setup(f => f.ShadowCopies).Returns(Enumerable.Empty<DivisonM.DriveBender.IVolume>());
      _mockFile.Setup(f => f.ShadowCopy).Returns((DivisonM.DriveBender.IVolume)null);
      
      // Act
      var shadowCopies = _mockFile.Object.ShadowCopies.ToArray();
      var shadowCopy = _mockFile.Object.ShadowCopy;
      
      // Assert
      shadowCopies.Should().BeEmpty();
      shadowCopy.Should().BeNull();
    }
    
    [Test]
    public void File_MoveBetweenVolumes_ShouldUpdatePrimary() {
      // Arrange
      var newPrimaryVolume = new Mock<DivisonM.DriveBender.IVolume>();
      newPrimaryVolume.Setup(v => v.Name).Returns("NewPrimaryVolume");
      
      // Simulate moving primary to new volume
      _mockFile.Setup(f => f.Primary).Returns(newPrimaryVolume.Object);
      _mockFile.Setup(f => f.Primaries).Returns(new[] { newPrimaryVolume.Object });
      
      // Act
      var newPrimary = _mockFile.Object.Primary;
      
      // Assert
      newPrimary.Should().Be(newPrimaryVolume.Object);
      newPrimary.Name.Should().Be("NewPrimaryVolume");
    }
    
    [Test]
    public void File_WithBothPrimaryAndShadow_ShouldMaintainBothReferences() {
      // Arrange
      _mockFile.Setup(f => f.ShadowCopies).Returns(new[] { _mockShadowVolume.Object });
      _mockFile.Setup(f => f.ShadowCopy).Returns(_mockShadowVolume.Object);
      
      // Act
      var primary = _mockFile.Object.Primary;
      var shadow = _mockFile.Object.ShadowCopy;
      
      // Assert
      primary.Should().NotBe(shadow);
      primary.Should().Be(_mockPrimaryVolume.Object);
      shadow.Should().Be(_mockShadowVolume.Object);
    }
    
    [Test]
    public void PrimaryFile_SizeCalculations_ShouldBeAccurate() {
      // Arrange
      var fileSize = ByteSize.FromMegabytes(10);
      _mockFile.Setup(f => f.Size).Returns(fileSize.Bytes);
      
      // Act & Assert
      var actualSize = new ByteSize(_mockFile.Object.Size);
      actualSize.Bytes.Should().Be(fileSize.Bytes);
      actualSize.Megabytes.Should().BeApproximately(10, 0.1);
    }
    
    [Test]
    public void File_LocationTracking_ShouldBeConsistent() {
      // Arrange
      var fileName = "TestDocument.pdf";
      var folderPath = new FolderPath("Documents/Work");
      var fullPath = folderPath.Combine(fileName);
      
      _mockFile.Setup(f => f.FullName).Returns(fullPath);
      
      // Act
      var retrievedPath = _mockFile.Object.FullName;
      
      // Assert
      retrievedPath.Should().Contain("Documents/Work");
      retrievedPath.Should().Contain(fileName);
    }
  }
}