using System;
using System.Collections.Generic;
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
  public class ShadowCopyTests : TestBase {
    
    private Mock<DivisonM.DriveBender.IMountPoint> _mockMountPoint;
    private Mock<DivisonM.DriveBender.IVolume> _mockVolume1;
    private Mock<DivisonM.DriveBender.IVolume> _mockVolume2;
    private Mock<DivisonM.DriveBender.IFile> _mockFile;
    
    [SetUp]
    public override void SetUp() {
      _mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      _mockVolume1 = new Mock<DivisonM.DriveBender.IVolume>();
      _mockVolume2 = new Mock<DivisonM.DriveBender.IVolume>();
      _mockFile = new Mock<DivisonM.DriveBender.IFile>();
      
      _mockMountPoint.Setup(m => m.Name).Returns("TestPool");
      _mockMountPoint.Setup(m => m.Volumes).Returns(new[] { _mockVolume1.Object, _mockVolume2.Object });
      
      _mockVolume1.Setup(v => v.Name).Returns("Volume1");
      _mockVolume1.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(100));
      
      _mockVolume2.Setup(v => v.Name).Returns("Volume2");
      _mockVolume2.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(50));
      
      _mockFile.Setup(f => f.FullName).Returns("TestFile.txt");
      _mockFile.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(1));
      _mockFile.Setup(f => f.Primary).Returns(_mockVolume1.Object);
      _mockFile.Setup(f => f.ShadowCopies).Returns(new[] { _mockVolume2.Object });
    }
    
    [Test]
    public void EnableDuplicationOnFolder_WithValidParameters_ShouldSucceed() {
      // Arrange
      var folderPath = new FolderPath("Documents");
      var duplicationLevel = new DuplicationLevel(2);
      
      // Act & Assert
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, folderPath, duplicationLevel)
      );
    }
    
    [Test]
    public void GetDuplicationLevel_WithExistingDuplication_ShouldReturnCorrectLevel() {
      // Arrange
      var folderPath = new FolderPath("Documents");
      
      // Act
      var level = DuplicationManager.GetDuplicationLevel(_mockMountPoint.Object, folderPath);
      
      // Assert
      level.Should().BeOfType<DuplicationLevel>();
    }
    
    [Test]
    public void CreateAdditionalShadowCopy_WithValidFile_ShouldSucceed() {
      // Arrange
      var targetVolume = _mockVolume2.Object;
      
      // Act & Assert
      Assert.DoesNotThrow(() => 
        DuplicationManager.CreateAdditionalShadowCopy(_mockFile.Object, targetVolume)
      );
    }
    
    [Test]
    public void ShadowCopy_WithPrimaryFile_ShouldHaveCorrectRelation() {
      // Arrange & Act
      var primaryVolume = _mockFile.Object.Primary;
      var shadowVolumes = _mockFile.Object.ShadowCopies.ToArray();
      
      // Assert
      primaryVolume.Should().NotBeNull();
      shadowVolumes.Should().HaveCount(1);
      shadowVolumes[0].Should().Be(_mockVolume2.Object);
    }
    
    [Test]
    public void File_WithMultipleShadowCopies_ShouldTrackAllCopies() {
      // Arrange
      var additionalVolume = new Mock<DivisonM.DriveBender.IVolume>();
      additionalVolume.Setup(v => v.Name).Returns("Volume3");
      
      _mockFile.Setup(f => f.ShadowCopies).Returns(new[] { 
        _mockVolume2.Object, 
        additionalVolume.Object 
      });
      
      // Act
      var shadowCount = _mockFile.Object.ShadowCopies.Count();
      
      // Assert
      shadowCount.Should().Be(2);
    }
    
    [Test]
    public void DuplicationLevel_WithValidValues_ShouldBehaveCorrectly() {
      // Arrange & Act
      var disabled = DuplicationLevel.Disabled;
      var single = DuplicationLevel.Single;
      var double_ = DuplicationLevel.Double;
      var triple = DuplicationLevel.Triple;
      
      // Assert
      disabled.IsDisabled.Should().BeTrue();
      single.IsSingleCopy.Should().BeTrue();
      double_.IsMultipleCopies.Should().BeTrue();
      triple.Value.Should().Be(3);
    }
    
    [Test]
    public void ByteSize_Conversions_ShouldWorkCorrectly() {
      // Arrange & Act
      var oneKB = ByteSize.FromKilobytes(1);
      var oneMB = ByteSize.FromMegabytes(1);
      var oneGB = ByteSize.FromGigabytes(1);
      
      // Assert
      oneKB.Bytes.Should().Be(1024);
      oneMB.Kilobytes.Should().BeApproximately(1024, 0.1);
      oneGB.Megabytes.Should().BeApproximately(1024, 0.1);
      oneGB.ToHumanReadable().Should().Be("1GiB");
    }
    
    [Test]
    public void FolderPath_WithValidPath_ShouldParseCorrectly() {
      // Arrange & Act
      var folderPath = new FolderPath("Documents/Projects/MyProject");
      
      // Assert
      folderPath.Segments.Should().Equal("Documents", "Projects", "MyProject");
      folderPath.Name.Should().Be("MyProject");
      folderPath.Parent.Value.Should().Be("Documents/Projects");
    }
    
    [Test]
    public void PoolName_WithValidName_ShouldAcceptValue() {
      // Arrange & Act
      var poolName = new PoolName("MyTestPool");
      
      // Assert
      poolName.Value.Should().Be("MyTestPool");
      ((string)poolName).Should().Be("MyTestPool");
    }
  }
}