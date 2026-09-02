# End-to-end coverage

What the shipped `dbmount` binary is actually exercised against, on both targets, through a
real filesystem driver and a real browser.

**This file is generated** by `.github/workflows/scripts/e2e-matrix.ps1` from the end-to-end
`.trx` results of the Windows and Linux CI jobs. Do not edit it by hand — a hand-kept matrix
drifts the moment a test is added or starts failing, and then quietly misleads.

Generated from run: [33639593273](https://github.com/Hawkynt/DriveBenderUtility/actions/runs/33639593273).

72 scenarios — 66 passing on at least one target, 0 failing.

| Area | Scenario | What it covers | Windows | Linux |
| --- | --- | --- | :---: | :---: |
| BitRot | `BitRot_GivenEveryCopyIsDamaged_ThenTheLossIsNotPassedOffAsGoodData` | Both copies rot differently: the pool must not silently hand back damaged data as if it were fine. | pass | not run |
| BitRot | `BitRot_GivenOneCopyIsSilentlyDamaged_ThenTheIntactContentIsStillServed` | One copy rots silently: the pool still serves the intact content rather than the damaged bytes. _(held back: Reads are not verified against the checksum database, so a silently damaged copy is served even though an intact one sits on the other member. Not a quick fix: the database holds WHOLE-FILE hashes, and a read serves a block, so there is nothing to check a block against without per-block checksums - a format change with a real cost. A scrub detects and repairs the damage; the exposure is the window before one runs. See docs/Issues.md.)_ | skipped | not run |
| BitRot | `BitRot_GivenTheBaselineWasTakenWhileMounted_ThenRotIsStillRepaired` | A deep health check run while the pool is MOUNTED still leaves a usable baseline, so later rot is repairable. | pass | not run |
| BitRot | `BitRot_WhenTheDeepHealthCheckRepairs_ThenBothCopiesAreIntactAgain` | A deep health check with --fix repairs the damaged copy from the intact one. | pass | not run |
| BitRot | `BitRot_WhenTheDeepHealthCheckRuns_ThenTheDamageIsReported` | A deep health check finds silent damage that a shallow one cannot, and reports it. | pass | not run |
| Boundary | `Create_GivenManyThreadsRaceForOnePath_ThenExactlyOneFileExistsAndItIsWhole` | Many threads creating the same new path at once: one wins, no exception escapes unexplained, and the file is whole. | pass | not run |
| Boundary | `Names_GivenLongPathsAndAwkwardNames_ThenTheyRemainAddressableAfterARemount` | A deep tree with long paths and awkward but legal names survives a remount with its content addressable. | pass | not run |
| Boundary | `Names_GivenTwoPathsDifferingOnlyInCase_ThenNoContentIsLost` | Two names differing only in case: on Windows they are one file, on Linux two, and in neither case does content go missing. | pass | not run |
| Boundary | `Reads_GivenTheyStraddleBlockBoundaries_ThenTheyReturnTheRightBytes` | Reads that straddle a page or block boundary return the right bytes, whatever offset and length they use. | pass | not run |
| Boundary | `Sizes_GivenFilesOnTheCacheAndBlockBoundaries_ThenEachRoundTripsExactly` | Files sized exactly on and either side of the page, buffer and block boundaries round-trip byte for byte. | pass | not run |
| Boundary | `Sparse_GivenAWriteFarBeyondTheEnd_ThenTheHoleReadsAsZeroes` | Writing far past the end of a file leaves a hole that reads as zeroes, and the bytes written land at the right offset. | pass | not run |
| Boundary | `Truncate_GivenAFileIsShrunkThenGrown_ThenTheOldContentDoesNotResurface` | A file shrunk and grown again reads as zeroes in the re-exposed region, never the content that used to be there. | pass | not run |
| Driver | `Append_GivenRepeatedOpens_ThenTheFileGrowsMonotonically` | Given Repeated Opens , then The File Grows Monotonically | pass | not run |
| Driver | `Concurrency_GivenManyReadersAndWritersThroughTheOs_ThenNoFileIsCorrupted` | Given Many Readers And Writers Through The Os , then No File Is Corrupted | pass | not run |
| Driver | `Directories_GivenATreeCreatedThroughTheOs_ThenItEnumeratesAndRemoves` | Given ATree Created Through The Os , then It Enumerates And Removes | pass | not run |
| Driver | `FreeSpace_GivenTheMountedVolume_ThenTheOsReportsPlausibleCapacity` | Given The Mounted Volume , then The Os Reports Plausible Capacity | pass | not run |
| Driver | `LargeFile_GivenAMultiMegabyteStream_ThenItRoundTripsThroughTheDriver` | Given AMulti Megabyte Stream , then It Round Trips Through The Driver | pass | not run |
| Driver | `Mount_WhenPoolIsMounted_ThenTheOsSeesAUsableFilesystem` | , when Pool Is Mounted , then The Os Sees AUsable Filesystem | pass | not run |
| Driver | `Rename_GivenFilesAndFolders_ThenBothMoveAndKeepTheirContent` | Given Files And Folders , then Both Move And Keep Their Content | pass | not run |
| Driver | `Seek_GivenRandomAccessWrites_ThenTheFileReflectsEveryUpdate` | Given Random Access Writes , then The File Reflects Every Update | pass | not run |
| Driver | `Truncate_GivenSetLength_ThenTheFileShrinksAndGrowsZeroFilled` | Given Set Length , then The File Shrinks And Grows Zero Filled | pass | not run |
| Driver | `WriteReadDelete_GivenAFileThroughTheOs_ThenItRoundTripsAndReachesEveryMember` | Given AFile Through The Os , then It Round Trips And Reaches Every Member | pass | not run |
| Durability | `Crash_GivenADeleteWasAcknowledged_ThenTheFileDoesNotComeBack` | A file deleted before a power cut stays deleted afterwards, rather than being resurrected from a member that had not caught up. | pass | not run |
| Durability | `Crash_GivenAMemberIsAlsoMissingAtRestart_ThenTheSurvivingCopyIsStillServed` | A power cut while a member is also missing: everything the surviving member holds is still served, and the pool comes back rather than refusing to start. | pass | not run |
| Durability | `Crash_GivenAnOverwriteWasInFlight_ThenNoByteIsFabricated` | A power cut in the middle of overwriting a file: every byte read back afterwards belongs to either the old or the new content, never to neither. | pass | not run |
| Durability | `Crash_GivenARenameWasInFlight_ThenTheFileExistsUnderExactlyOneName` | A power cut during a rename: the file is at one of the two names with its content intact, never at neither. | pass | not run |
| Durability | `Crash_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvivesThePowerCut` | A power cut after files were written and closed: every byte is still there after the pool comes back. | pass | not run |
| Durability | `Crash_GivenStagedWritesWereInterrupted_ThenNoInternalFileIsExposedToTheUser` | A power cut leaves half-written staging files on the members; none of them may show up in the pool as if they were the user's. | pass | not run |
| Durability | `Divergence_GivenEachMemberTookAWriteWhileTheOtherWasAway_ThenOneWholeVersionIsServed` | Each member took a write while the other was away: the pool serves one whole version, never a mixture of the two. | pass | not run |
| LargeFile | `LargeFile_GivenItExceedsTwoGiB_ThenItsLengthIsReportedInFull` | A file larger than 2 GiB reports its true length rather than a 32-bit wrapped one. | pass | not run |
| LargeFile | `LargeFile_GivenReadsAroundTheThirtyTwoBitBoundaries_ThenEveryByteIsCorrect` | Reads on both sides of the 2 GiB and 4 GiB-relevant boundaries return the right bytes. | pass | not run |
| LargeFile | `LargeFile_WhenAppendedTo_ThenTheNewBytesLandPastTheOldEnd` | Appending to a file that is already past 2 GiB puts the bytes at the true end, not at a wrapped offset. | pass | not run |
| LargeFile | `LargeFile_WhenStreamedEndToEnd_ThenMemoryStaysBoundedAndThroughputHolds` | A file past 2 GiB streams rather than being materialised: the mount's memory stays far below the file size, and throughput stays reasonable. | pass | not run |
| LargeFile | `LargeFile_WhenWritten_ThenThroughputHoldsAndDoesNotDegradeWithSize` | Writing a file past 2 GiB sustains a sensible rate and does not slow down as the file grows. | pass | not run |
| LargeFile | `LargeFile_WhenWrittenInThePastTwoGiBRegion_ThenOnlyThatRegionChanges` | Writing into the middle of a file past 2 GiB changes only that region. | pass | not run |
| ManagementApi | `Api_GivenNoToken_ThenEveryEndpointRefuses` | Given No Token , then Every Endpoint Refuses | pass | not run |
| ManagementApi | `Assets_GivenAFreshBrowser_ThenThePageAndItsScriptAndStylesAreServed` | Given AFresh Browser , then The Page And Its Script And Styles Are Served | pass | not run |
| ManagementApi | `Job_GivenAnUnknownTicket_ThenItIsReportedRatherThanHanging` | Given An Unknown Ticket , then It Is Reported Rather Than Hanging | pass | not run |
| ManagementApi | `LongOperation_GivenTheDriverInstallEndpoint_ThenItAnswersImmediatelyWithATicket` | Given The Driver Install Endpoint , then It Answers Immediately With ATicket | pass | not run |
| ManagementApi | `PoolLifecycle_GivenCreateThenForget_ThenTheDashboardReflectsBothWithoutAMount` | Given Create , then Forget , then The Dashboard Reflects Both Without AMount | pass | not run |
| ManagementApi | `Pools_GivenTheDashboardFrame_ThenItIsWellFormedAndCarriesTheJobList` | Given The Dashboard Frame , then It Is Well Formed And Carries The Job List | pass | not run |
| ManagementApi | `Prereqs_GivenThisMachine_ThenTheDriverStatusIsReportedHonestly` | Given This Machine , then The Driver Status Is Reported Honestly | pass | not run |
| ManagementApi | `Stream_GivenAConnectedClient_ThenLiveFramesArrive` | Given AConnected Client , then Live Frames Arrive | pass | not run |
| MemberLoss | `Capacity_GivenAMemberIsPulledMidWrite_ThenTheReportedSpaceNeverCountsTheLostStorage` | A member pulled mid-write: the pool reports less free space afterwards and never claims the lost disk's capacity. | pass | not run |
| MemberLoss | `Capacity_GivenDuplicationIsOn_ThenStoringAFileCostsTwiceItsSize` | Duplication charges twice: storing N bytes with two copies consumes about 2N of the pool's free space. | pass | not run |
| MemberLoss | `Capacity_GivenTheMountedPool_ThenTheReportedSizeTracksTheStorageBehindIt` | Given The Mounted Pool , then The Reported Size Tracks The Storage Behind It | pass | not run |
| MemberLoss | `Duplication_GivenTheConfiguredPool_ThenEveryFileReallyExistsTwice` | Given The Configured Pool , then Every File Really Exists Twice | pass | not run |
| MemberLoss | `Eject_GivenAFileIsDeletedWhileAMemberIsAway_ThenItDoesNotResurrectOnItsReturn` | Given AFile Is Deleted While AMember Is Away , then It Does Not Resurrect On Its Return | pass | not run |
| MemberLoss | `Eject_GivenAMemberIsAway_ThenWritesStillSucceedAndHealWhenItReturns` | Given AMember Is Away , then Writes Still Succeed And Heal , when It Returns | pass | not run |
| MemberLoss | `Eject_GivenAMemberIsPulled_ThenExistingFilesStayReadableFromTheSurvivor` | Given AMember Is Pulled , then Existing Files Stay Readable From The Survivor | pass | not run |
| MemberLoss | `Eject_GivenAMemberReturnsWhileIoIsInFlight_ThenNothingIsCorruptedOrStalled` | Given AMember Returns While Io Is In Flight , then Nothing Is Corrupted Or Stalled | pass | not run |
| MemberLoss | `Eject_GivenAMemberVanishesDuringAWrite_ThenTheDataThatWasAcknowledgedIsIntact` | Given AMember Vanishes During AWrite , then The Data That Was Acknowledged Is Intact | pass | not run |
| MemberLoss | `Eject_GivenEveryMemberIsGone_ThenOperationsFailCleanlyInsteadOfHanging` | Given Every Member Is Gone , then Operations Fail Cleanly Instead Of Hanging | pass | not run |
| PerformanceMatrix | `Large_RandomReadIops_AcrossTiersAndConcurrency` | Random 4 KiB read IOPS on a large file, from cache and from storage, single- and multi-threaded. | skipped | not run |
| PerformanceMatrix | `Large_SequentialThroughput_AcrossTiers` | Sequential throughput for a 1.5 GiB file: written, read back warm from cache, and read cold from storage. | skipped | not run |
| PerformanceMatrix | `Scatter_OverlappedIoAcrossStorages` | What overlapping the block loads is worth: one storage held at queue depth 1, the same storage overlapped, and a file whose two copies are read together. | skipped | not run |
| PerformanceMatrix | `Small_FileIops_AcrossConcurrency` | Small-file (<4 KiB) create/write/close and read IOPS, single- and multi-threaded. | skipped | not run |
| SharedAccess | `Durability_GivenAnUnmountAndRemount_ThenEverythingWrittenIsStillThere` | Given An Unmount And Remount , then Everything Written Is Still There | pass | not run |
| SharedAccess | `Namespace_GivenParallelCreateRenameDelete_ThenTheDirectoryStaysConsistent` | Given Parallel Create Rename Delete , then The Directory Stays Consistent | pass | not run |
| SharedAccess | `SharedFile_GivenAppendersOnSeparateFiles_ThenEveryByteSurvives` | Given Appenders On Separate Files , then Every Byte Survives | pass | not run |
| SharedAccess | `SharedFile_GivenAReaderHoldsItOpenWhileItIsRenamed_ThenNeitherSideIsCorrupted` | Given AReader Holds It Open While It Is Renamed , then Neither Side Is Corrupted | pass | not run |
| SharedAccess | `SharedFile_GivenConcurrentReadersOnOneOpenFile_ThenEachSeesTheWholeContent` | Given Concurrent Readers On One Open File , then Each Sees The Whole Content | pass | not run |
| SharedAccess | `SharedFile_GivenWritersOwningDisjointRegionsOfOneFile_ThenNoRegionIsCorruptedByAnother` | Given Writers Owning Disjoint Regions Of One File , then No Region Is Corrupted By Another | pass | not run |
| SharedAccess | `SharedFile_GivenWritersReplacingItByRename_ThenEveryReadIsAWholeVersion` | Given Writers Replacing It By Rename , then Every Read Is AWhole Version _(held back: A file replaced by rename keeps serving its OLD content to readers that hold the name open. Measured again this pass: 16 replacements landed, all 3,200 reads returned version 1, and the read taken after the workers stopped returned version 60 - so the data is correct on disk and the staleness is tied to concurrent handles, not a permanent failure to invalidate. Setting FspFileInfo.IndexNumber to a real per-file identity was tried and does NOT fix it. See docs/Issues.md.)_ | skipped | not run |
| Tiering | `Tiering_GivenAFileHasDrained_ThenTheFastTierIsFreedAgain` | The fast tier is freed again after a file drains, so a landing zone does not fill up permanently. | pass | not run |
| Tiering | `Tiering_GivenAFileIsWritten_ThenItLandsOnTheFastTierAndDrainsToCapacity` | New data lands on the fast tier first, then the drainer moves it down to capacity storage on its own. | pass | not run |
| Tiering | `Tiering_GivenALandingZone_ThenWritesAreAcceptedAndReadBackIntact` | A landing-zone pool accepts writes and serves them back correctly through the mount. | pass | not run |
| Tiering | `Tiering_WhileTheMoverIsRelocatingFiles_ThenTheyStayReadableAndWritable` | Tiering is transparent: a file stays readable AND writable throughout, including while the mover is relocating it. | pass | not run |
| WebUi | `Dashboard_WhenAPoolIsPresent_ThenItsActionsAreOffered` | , when APool Is Present , then Its Actions Are Offered | pass | not run |
| WebUi | `Dashboard_WhenOpenedWithoutAToken_ThenItDoesNotLeakPoolData` | , when Opened Without AToken , then It Does Not Leak Pool Data | pass | not run |
| WebUi | `Dashboard_WhenOpenedWithTheToken_ThenItRendersThePoolWithoutScriptErrors` | , when Opened With The Token , then It Renders The Pool Without Script Errors | pass | not run |
| WebUi | `Dashboard_WhenTheLiveStreamConnects_ThenTheIndicatorReportsItAsLive` | , when The Live Stream Connects , then The Indicator Reports It As Live | pass | not run |

`skipped` marks a scenario the platform cannot express or one deliberately held back against a
known defect — the reason travels with the test, in its `Assert.Ignore`/`[Ignore]` text.
