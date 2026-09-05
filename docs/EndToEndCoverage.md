# End-to-end coverage

What the shipped `dbmount` binary is actually exercised against, on both targets, through a
real filesystem driver and a real browser.

**This file is generated** by `.github/workflows/scripts/e2e-matrix.ps1` from the end-to-end
`.trx` results of the Windows and Linux CI jobs. Do not edit it by hand — a hand-kept matrix
drifts the moment a test is added or starts failing, and then quietly misleads.

Generated from run: [33976678588](https://github.com/Hawkynt/DriveBenderUtility/actions/runs/33976678588).

169 scenarios — 148 passing on at least one target, 0 failing.

| Area | Scenario | What it covers | Windows | Linux |
| --- | --- | --- | :---: | :---: |
| BackgroundRace | `Delete_WhileACopyIsStillInFlight_ThenTheFileDoesNotComeBack` | A file deleted while the pool is still copying it stays deleted, rather than reappearing when the copy lands. | pass | pass |
| BackgroundRace | `Overwrite_WhileTheHealerIsCopyingTheOldContent_ThenBothCopiesEndOnTheNewOne` | A file overwritten while the healer is copying the OLD content to a returning member ends with both copies on the NEW content. | pass | pass |
| BackgroundRace | `Read_GivenAReturnedMemberLostItsCopies_ThenEveryFileIsStillServedFromTheSurvivor` | A member returns having lost its copies: every file is still readable at once from the surviving copy, without waiting for the heal. | pass | pass |
| BackgroundRace | `Read_WhileTheHealerIsCopying_ThenItIsServedAtOnceRatherThanAtTheCopysPace` | A file stays readable at full speed while the healer is copying it to another member. | pass | pass |
| BackgroundRace | `Rename_WhileACopyIsStillInFlight_ThenItEndsUnderExactlyOneName` | A file renamed while the pool is still copying it ends under exactly one name, with its content intact. | pass | pass |
| BitRot | `BitRot_GivenEveryCopyIsDamaged_ThenTheLossIsNotPassedOffAsGoodData` | Both copies rot differently: the pool must not silently hand back damaged data as if it were fine. | pass | pass |
| BitRot | `BitRot_GivenOneCopyIsSilentlyDamaged_ThenTheIntactContentIsStillServed` | One copy rots silently: the pool still serves the intact content rather than the damaged bytes. _(held back: Reads are not verified against the checksum database, so a silently damaged copy is served even though an intact one sits on the other member. Not a quick fix: the database holds WHOLE-FILE hashes, and a read serves a block, so there is nothing to check a block against without per-block checksums - a format change with a real cost. A scrub detects and repairs the damage; the exposure is the window before one runs. See docs/Issues.md.)_ | skipped | skipped |
| BitRot | `BitRot_GivenTheBaselineWasTakenWhileMounted_ThenRotIsStillRepaired` | A deep health check run while the pool is MOUNTED still leaves a usable baseline, so later rot is repairable. | pass | pass |
| BitRot | `BitRot_WhenTheDeepHealthCheckRepairs_ThenBothCopiesAreIntactAgain` | A deep health check with --fix repairs the damaged copy from the intact one. | pass | pass |
| BitRot | `BitRot_WhenTheDeepHealthCheckRuns_ThenTheDamageIsReported` | A deep health check finds silent damage that a shallow one cannot, and reports it. | pass | pass |
| Boundary | `Create_GivenManyThreadsRaceForOnePath_ThenExactlyOneFileExistsAndItIsWhole` | Many threads creating the same new path at once: one wins, no exception escapes unexplained, and the file is whole. | pass | pass |
| Boundary | `Names_GivenLongPathsAndAwkwardNames_ThenTheyRemainAddressableAfterARemount` | A deep tree with long paths and awkward but legal names survives a remount with its content addressable. | pass | pass |
| Boundary | `Names_GivenTwoPathsDifferingOnlyInCase_ThenNoContentIsLost` | Two names differing only in case: on Windows they are one file, on Linux two, and in neither case does content go missing. | pass | pass |
| Boundary | `Reads_GivenTheyStraddleBlockBoundaries_ThenTheyReturnTheRightBytes` | Reads that straddle a page or block boundary return the right bytes, whatever offset and length they use. | pass | pass |
| Boundary | `Sizes_GivenFilesOnTheCacheAndBlockBoundaries_ThenEachRoundTripsExactly` | Files sized exactly on and either side of the page, buffer and block boundaries round-trip byte for byte. | pass | pass |
| Boundary | `Sparse_GivenAWriteFarBeyondTheEnd_ThenTheHoleReadsAsZeroes` | Writing far past the end of a file leaves a hole that reads as zeroes, and the bytes written land at the right offset. | pass | pass |
| Boundary | `Truncate_GivenAFileIsShrunkThenGrown_ThenTheOldContentDoesNotResurface` | A file shrunk and grown again reads as zeroes in the re-exposed region, never the content that used to be there. | pass | pass |
| Brownout | `Brownout_GivenOneOfTwoCapacityMembersCollapses_ThenNewFilesGoToTheHealthyOne` | With two capacity members and one collapsed, new files are placed on the healthy one rather than spread evenly into the wall. | pass | pass |
| Brownout | `Brownout_GivenSomeoneTriesToWeakenTheAckFloor_ThenThePoolRefusesToMount` | Weakening a duplicated write's ack floor to one copy is refused outright, so the pacing above cannot be configured away by accident. | pass | pass |
| Brownout | `Brownout_GivenTheDefaultAckPolicy_ThenADuplicatedWriteIsPacedByTheSickCopy` | Under the default ack policy a duplicated write IS paced by its slowest copy — the durability promise costs exactly that. | pass | pass |
| Brownout | `Brownout_GivenThePrimaryCopysMemberCollapses_ThenReadsAreServedFromTheHealthyCopy` | The member holding the primary copy collapses to a crawl: reads must be served from the healthy copy instead of crawling with it. | pass | pass |
| Brownout | `Brownout_GivenTheVolatileAckOptIn_ThenTheWriteIsNotPacedByTheSickCopy` | The RAM-ack opt-in is the sanctioned way out: the write is taken at memory speed and both copies converge behind it. | pass | pass |
| Brownout | `Brownout_WhenAMembersLimitIsLoweredLive_ThenItTakesEffectWithoutARemount` | A rate limit lowered on a mounted pool takes effect without a remount, rather than being ignored until the next mount. | pass | pass |
| Brownout | `Brownout_WhenTheMemberRecovers_ThenThroughputComesBack` | When the collapsed member recovers, the pool's throughput comes back rather than staying degraded. | pass | pass |
| DrainCrash | `Crash_GivenADrainWasInFlight_ThenNoStagingFileIsExposed` | A crash mid-drain leaves no half-written staging file visible to the user after the pool comes back. | pass | pass |
| DrainCrash | `Crash_GivenADrainWasInFlight_ThenTheFileSurvivesWholeOnOneTier` | The power goes off while the drainer is copying a file down to capacity: the file comes back whole, on one tier or the other. | pass | pass |
| DrainCrash | `Recovery_GivenOnePathOnTwoMembers_ThenThePoolServesItOnceAndWhole` | The same file left on two members, as a crash between a relocation's copy and its delete leaves it: the pool serves one entry, not two. | pass | pass |
| Driver | `Append_GivenRepeatedOpens_ThenTheFileGrowsMonotonically` | Given Repeated Opens , then The File Grows Monotonically | pass | pass |
| Driver | `Concurrency_GivenManyReadersAndWritersThroughTheOs_ThenNoFileIsCorrupted` | Given Many Readers And Writers Through The Os , then No File Is Corrupted | pass | pass |
| Driver | `Directories_GivenATreeCreatedThroughTheOs_ThenItEnumeratesAndRemoves` | Given ATree Created Through The Os , then It Enumerates And Removes | pass | pass |
| Driver | `FreeSpace_GivenTheMountedVolume_ThenTheOsReportsPlausibleCapacity` | Given The Mounted Volume , then The Os Reports Plausible Capacity | pass | skipped |
| Driver | `LargeFile_GivenAMultiMegabyteStream_ThenItRoundTripsThroughTheDriver` | Given AMulti Megabyte Stream , then It Round Trips Through The Driver | pass | pass |
| Driver | `Mount_WhenPoolIsMounted_ThenTheOsSeesAUsableFilesystem` | , when Pool Is Mounted , then The Os Sees AUsable Filesystem | pass | pass |
| Driver | `Rename_GivenFilesAndFolders_ThenBothMoveAndKeepTheirContent` | Given Files And Folders , then Both Move And Keep Their Content | pass | pass |
| Driver | `Seek_GivenRandomAccessWrites_ThenTheFileReflectsEveryUpdate` | Given Random Access Writes , then The File Reflects Every Update | pass | pass |
| Driver | `Truncate_GivenSetLength_ThenTheFileShrinksAndGrowsZeroFilled` | Given Set Length , then The File Shrinks And Grows Zero Filled | pass | pass |
| Driver | `WriteReadDelete_GivenAFileThroughTheOs_ThenItRoundTripsAndReachesEveryMember` | Given AFile Through The Os , then It Round Trips And Reaches Every Member | pass | pass |
| Durability | `Crash_GivenADeleteWasAcknowledged_ThenTheFileDoesNotComeBack` | A file deleted before a power cut stays deleted afterwards, rather than being resurrected from a member that had not caught up. | pass | pass |
| Durability | `Crash_GivenAMemberIsAlsoMissingAtRestart_ThenTheSurvivingCopyIsStillServed` | A power cut while a member is also missing: everything the surviving member holds is still served, and the pool comes back rather than refusing to start. | pass | pass |
| Durability | `Crash_GivenAnOverwriteWasInFlight_ThenNoByteIsFabricated` | A power cut in the middle of overwriting a file: every byte read back afterwards belongs to either the old or the new content, never to neither. | pass | pass |
| Durability | `Crash_GivenARenameWasInFlight_ThenTheFileExistsUnderExactlyOneName` | A power cut during a rename: the file is at one of the two names with its content intact, never at neither. | pass | pass |
| Durability | `Crash_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvivesThePowerCut` | A power cut after files were written and closed: every byte is still there after the pool comes back. | pass | pass |
| Durability | `Crash_GivenStagedWritesWereInterrupted_ThenNoInternalFileIsExposedToTheUser` | A power cut leaves half-written staging files on the members; none of them may show up in the pool as if they were the user's. | pass | pass |
| Durability | `Divergence_GivenEachMemberTookAWriteWhileTheOtherWasAway_ThenOneWholeVersionIsServed` | Each member took a write while the other was away: the pool serves one whole version, never a mixture of the two. | pass | pass |
| FolderRenameRace | `RenameFolder_WhileAChildIsBeingWritten_ThenNoAcknowledgedWriteIsLost` | A folder renamed under files that are being written: no write the pool acknowledged may go missing. | skipped | pass |
| HeterogeneousDevice | `Duplication_GivenOneCopyOnEachDevice_ThenBothCopiesAreWhole` | A file mirrored across a fast and a slow disk is byte-identical on both, whichever of them took it first. | skipped | skipped |
| HeterogeneousDevice | `Duplication_GivenOneCopyOnEachDevice_ThenReadsAreNotHeldToTheSlowDisksPace` | With one copy on a fast disk and one on a slow one, reading the file is not held to the slow disk's pace. | skipped | skipped |
| HeterogeneousDevice | `Health_GivenAMemberOnARealDevice_ThenItsSmartStateReachesTheSnapshot` | A member on a real block device reports that device's SMART health into the live snapshot the dashboard reads. | skipped | skipped |
| HeterogeneousDevice | `SlowMember_GivenItComesAndGoesRepeatedly_ThenNothingIsLostAndThePoolStaysResponsive` | A removable disk that comes and goes repeatedly leaves the pool with every file whole and still responsive. | pass | skipped |
| HeterogeneousDevice | `SlowMember_WhenItIsPulledMidWrite_ThenTheWriteFinishesWithoutStalling` | Pulling the slow disk out from under a live write does not stall the pool: the write finishes at the fast disk's pace. | skipped | skipped |
| HeterogeneousDevice | `SlowMember_WhenItRunsCompletelyOutOfSpace_ThenTheRefusalIsCleanAndStoredDataIsIntact` | Filling the only disk in a pool right up is refused cleanly, and everything already stored stays readable and whole. | skipped | skipped |
| HeterogeneousDevice | `Tiering_GivenTheCapacityDiskIsGenuinelySlow_ThenAWriteBurstRunsAtTheFastTiersPace` | With a genuinely slow capacity disk behind a fast landing zone, a write burst still runs at the fast tier's pace rather than the slow disk's. | skipped | skipped |
| HeterogeneousDevice | `Tiering_WhenTheBurstDrainsDownToTheSlowDisk_ThenEveryByteArrivesIntact` | Everything the fast tier absorbed arrives byte-for-byte on the slow capacity disk when the drainer moves it down. | skipped | skipped |
| LargeFile | `LargeFile_GivenItExceedsTwoGiB_ThenItsLengthIsReportedInFull` | A file larger than 2 GiB reports its true length rather than a 32-bit wrapped one. | pass | pass |
| LargeFile | `LargeFile_GivenReadsAroundTheThirtyTwoBitBoundaries_ThenEveryByteIsCorrect` | Reads on both sides of the 2 GiB and 4 GiB-relevant boundaries return the right bytes. | pass | pass |
| LargeFile | `LargeFile_WhenAppendedTo_ThenTheNewBytesLandPastTheOldEnd` | Appending to a file that is already past 2 GiB puts the bytes at the true end, not at a wrapped offset. | pass | pass |
| LargeFile | `LargeFile_WhenStreamedEndToEnd_ThenMemoryStaysBoundedAndThroughputHolds` | A file past 2 GiB streams rather than being materialised: the mount's memory stays far below the file size, and throughput stays reasonable. | pass | pass |
| LargeFile | `LargeFile_WhenWritten_ThenThroughputHoldsAndDoesNotDegradeWithSize` | Writing a file past 2 GiB sustains a sensible rate and does not slow down as the file grows. | pass | pass |
| LargeFile | `LargeFile_WhenWrittenInThePastTwoGiBRegion_ThenOnlyThatRegionChanges` | Writing into the middle of a file past 2 GiB changes only that region. | pass | pass |
| ManagementApi | `Api_GivenNoToken_ThenEveryEndpointRefuses` | Given No Token , then Every Endpoint Refuses | pass | pass |
| ManagementApi | `Assets_GivenAFreshBrowser_ThenThePageAndItsScriptAndStylesAreServed` | Given AFresh Browser , then The Page And Its Script And Styles Are Served | pass | pass |
| ManagementApi | `Job_GivenAnUnknownTicket_ThenItIsReportedRatherThanHanging` | Given An Unknown Ticket , then It Is Reported Rather Than Hanging | pass | pass |
| ManagementApi | `LongOperation_GivenTheDriverInstallEndpoint_ThenItAnswersImmediatelyWithATicket` | Given The Driver Install Endpoint , then It Answers Immediately With ATicket | pass | pass |
| ManagementApi | `MemberLimits_GivenAnUnknownMember_ThenItIsRefusedRatherThanSilentlyIgnored` | Given An Unknown Member , then It Is Refused Rather Than Silently Ignored | pass | pass |
| ManagementApi | `MemberLimits_GivenEachShapeInTurn_ThenTheDashboardReportsWhatWasSet` | Given Each Shape In Turn , then The Dashboard Reports What Was Set | pass | pass |
| ManagementApi | `PoolLifecycle_GivenCreateThenForget_ThenTheDashboardReflectsBothWithoutAMount` | Given Create , then Forget , then The Dashboard Reflects Both Without AMount | pass | pass |
| ManagementApi | `Pools_GivenTheDashboardFrame_ThenItIsWellFormedAndCarriesTheJobList` | Given The Dashboard Frame , then It Is Well Formed And Carries The Job List | pass | pass |
| ManagementApi | `Prereqs_GivenThisMachine_ThenTheDriverStatusIsReportedHonestly` | Given This Machine , then The Driver Status Is Reported Honestly | pass | pass |
| ManagementApi | `Stream_GivenAConnectedClient_ThenLiveFramesArrive` | Given AConnected Client , then Live Frames Arrive | pass | pass |
| MemberFailureLatency | `Cripple_GivenAMemberFailsEveryOperationWithoutGoingOffline_ThenReadsStillCompletePromptly` | A member that is still present but fails every operation is routed around: reads keep completing promptly from the healthy copy. | skipped | pass |
| MemberFailureLatency | `Eject_GivenEveryMemberGoesAndOneComesBack_ThenItsContentIsServedAgain` | Every member goes away and one comes back: its content is served again rather than the pool staying dark. | pass | pass |
| MemberFailureLatency | `Eject_WhileALargeReadIsStreaming_ThenEveryRemainingChunkStillArrivesPromptly` | A member pulled while a large read is streaming: every remaining chunk still arrives promptly and the content is whole. | pass | pass |
| MemberFailureLatency | `Eject_WhileTheDrainerIsMovingAFileDown_ThenTheFileIsNeverLost` | The capacity disk is pulled while the drainer is moving a file down to it: the file is on one tier or the other, never on neither, and comes back whole. | pass | pass |
| MemberFailureLatency | `Mount_GivenThePoolIsAlreadyMounted_ThenASecondMountIsRefused` | A pool already mounted refuses to be mounted a second time, because two engines over one member set corrupt each other. | pass | pass |
| MemberFailureLatency | `ReadOnly_GivenADuplicatedPool_ThenEverythingStoredIsStillServedPromptly` | A DUPLICATED pool whose member goes read-only keeps serving every stored byte promptly, which is the half that must never regress. | skipped | pass |
| MemberFailureLatency | `ReadOnly_GivenTheChosenMemberRefusesWrites_ThenNewFilesGoToOneThatDoesNot` | An UNDUPLICATED pool whose placement target goes read-only still takes new files, by putting them on a member that can accept them. | skipped | pass |
| MemberFailureLatency | `Unmount_GivenItIsAskedForTheMomentThePoolIsUsable_ThenItSucceedsAndTheMountIsGone` | A pool unmounted immediately after it comes up really unmounts, rather than the verb reporting a pool it cannot find. | pass | pass |
| MemberLoss | `Capacity_GivenAMemberIsPulledMidWrite_ThenTheReportedSpaceNeverCountsTheLostStorage` | A member pulled mid-write: the pool reports less free space afterwards and never claims the lost disk's capacity. | pass | skipped |
| MemberLoss | `Capacity_GivenDuplicationIsOn_ThenStoringAFileCostsTwiceItsSize` | Duplication charges twice: storing N bytes with two copies consumes about 2N of the pool's free space. | pass | skipped |
| MemberLoss | `Capacity_GivenTheMountedPool_ThenTheReportedSizeTracksTheStorageBehindIt` | Given The Mounted Pool , then The Reported Size Tracks The Storage Behind It | pass | skipped |
| MemberLoss | `Duplication_GivenTheConfiguredPool_ThenEveryFileReallyExistsTwice` | Given The Configured Pool , then Every File Really Exists Twice | pass | pass |
| MemberLoss | `Eject_GivenAFileIsDeletedWhileAMemberIsAway_ThenItDoesNotResurrectOnItsReturn` | Given AFile Is Deleted While AMember Is Away , then It Does Not Resurrect On Its Return | pass | pass |
| MemberLoss | `Eject_GivenAMemberIsAway_ThenWritesStillSucceedAndHealWhenItReturns` | Given AMember Is Away , then Writes Still Succeed And Heal , when It Returns | pass | pass |
| MemberLoss | `Eject_GivenAMemberIsPulled_ThenExistingFilesStayReadableFromTheSurvivor` | Given AMember Is Pulled , then Existing Files Stay Readable From The Survivor | pass | pass |
| MemberLoss | `Eject_GivenAMemberReturnsWhileIoIsInFlight_ThenNothingIsCorruptedOrStalled` | Given AMember Returns While Io Is In Flight , then Nothing Is Corrupted Or Stalled | pass | pass |
| MemberLoss | `Eject_GivenAMemberVanishesDuringAWrite_ThenTheDataThatWasAcknowledgedIsIntact` | Given AMember Vanishes During AWrite , then The Data That Was Acknowledged Is Intact | pass | pass |
| MemberLoss | `Eject_GivenEveryMemberIsGone_ThenOperationsFailCleanlyInsteadOfHanging` | Given Every Member Is Gone , then Operations Fail Cleanly Instead Of Hanging | pass | pass |
| MultiDeviceThroughput | `Scatter_GivenAMirrorAcrossTwoDevices_ThenItReadsFasterThanTheSameMirrorOnOne` | Prices a mirror spread across two independent devices against the same mirror stacked on one. | skipped | skipped |
| MultiDeviceThroughput | `Scatter_GivenATierOnTwoDevices_ThenTheBurstIsSpreadAcrossBoth` | A tier built from two independent devices spreads a write burst across both of them, which is the precondition for combining their throughput at all. | skipped | skipped |
| PerformanceMatrix | `Large_RandomReadIops_AcrossTiersAndConcurrency` | Random 4 KiB read IOPS on a large file, from cache and from storage, single- and multi-threaded. | skipped | skipped |
| PerformanceMatrix | `Large_SequentialThroughput_AcrossTiers` | Sequential throughput for a 1.5 GiB file: written, read back warm from cache, and read cold from storage. | skipped | skipped |
| PerformanceMatrix | `Scatter_OverlappedIoAcrossStorages` | What overlapping the block loads is worth: one storage held at queue depth 1, the same storage overlapped, and a file whose two copies are read together. | skipped | skipped |
| PerformanceMatrix | `Small_FileIops_AcrossConcurrency` | Small-file (<4 KiB) create/write/close and read IOPS, single- and multi-threaded. | skipped | skipped |
| SharedAccess | `Durability_GivenAnUnmountAndRemount_ThenEverythingWrittenIsStillThere` | Given An Unmount And Remount , then Everything Written Is Still There | pass | pass |
| SharedAccess | `Namespace_GivenParallelCreateRenameDelete_ThenTheDirectoryStaysConsistent` | Given Parallel Create Rename Delete , then The Directory Stays Consistent | pass | pass |
| SharedAccess | `SharedFile_GivenAppendersOnSeparateFiles_ThenEveryByteSurvives` | Given Appenders On Separate Files , then Every Byte Survives | pass | pass |
| SharedAccess | `SharedFile_GivenAReaderHoldsItOpenWhileItIsRenamed_ThenNeitherSideIsCorrupted` | Given AReader Holds It Open While It Is Renamed , then Neither Side Is Corrupted | pass | pass |
| SharedAccess | `SharedFile_GivenConcurrentReadersOnOneOpenFile_ThenEachSeesTheWholeContent` | Given Concurrent Readers On One Open File , then Each Sees The Whole Content | pass | pass |
| SharedAccess | `SharedFile_GivenWritersOwningDisjointRegionsOfOneFile_ThenNoRegionIsCorruptedByAnother` | Given Writers Owning Disjoint Regions Of One File , then No Region Is Corrupted By Another | pass | pass |
| SharedAccess | `SharedFile_GivenWritersReplacingItByRename_ThenEveryReadIsAWholeVersion` | Given Writers Replacing It By Rename , then Every Read Is AWhole Version _(held back: A file replaced by rename keeps serving its OLD content to readers that hold the name open. Measured again this pass: 16 replacements landed, all 3,200 reads returned version 1, and the read taken after the workers stopped returned version 60 - so the data is correct on disk and the staleness is tied to concurrent handles, not a permanent failure to invalidate. Setting FspFileInfo.IndexNumber to a real per-file identity was tried and does NOT fix it. See docs/Issues.md.)_ | skipped | skipped |
| SimulatedDevice | `Duplication_GivenOneCopyOnEachSpeed_ThenReadsAreNotHeldToTheSlowOne(HDD over cloud)` | Given One Copy On Each Speed , then Reads Are Not Held To The Slow One(HDD over cloud) | pass | pass |
| SimulatedDevice | `Duplication_GivenOneCopyOnEachSpeed_ThenReadsAreNotHeldToTheSlowOne(RAM over cloud)` | Given One Copy On Each Speed , then Reads Are Not Held To The Slow One(RAM over cloud) | pass | pass |
| SimulatedDevice | `Duplication_GivenOneCopyOnEachSpeed_ThenReadsAreNotHeldToTheSlowOne(RAM over SD card)` | Given One Copy On Each Speed , then Reads Are Not Held To The Slow One(RAM over SD card) | pass | pass |
| SimulatedDevice | `Duplication_GivenOneCopyOnEachSpeed_ThenReadsAreNotHeldToTheSlowOne(SSD over HDD)` | Given One Copy On Each Speed , then Reads Are Not Held To The Slow One(SSD over HDD) | pass | pass |
| SimulatedDevice | `Duplication_GivenOneCopyOnEachSpeed_ThenReadsAreNotHeldToTheSlowOne(SSD over SD card)` | Given One Copy On Each Speed , then Reads Are Not Held To The Slow One(SSD over SD card) | pass | pass |
| SimulatedDevice | `Limits_GivenBackgroundIsStarvedOnTheLandingZone_ThenTheApplicationIsNotHeldToIt` | Starving the pool's own background copying does not starve the application: writes stay fast while the drain crawls. | pass | pass |
| SimulatedDevice | `Read_GivenTheFileIsMidDrain_ThenItIsServedAtOnceRatherThanAtTheDrainsPace` | A file stays readable at full speed while the pool is relocating it, even when that relocation is throttled to a crawl. | pass | pass |
| SimulatedDevice | `Throttle_GivenAMemberLimitedToAByteRate_ThenTheMountIsHeldToIt` | A member the manifest limits to a byte rate really is held to it through a real mount, rather than the limit being decoration. | pass | pass |
| SimulatedDevice | `Throttle_GivenNoLimit_ThenTheSamePoolIsFarFaster` | The same pool without the limit is far faster, so the limit is what the previous scenario measured and not the host. | pass | pass |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstLandsOnTheFastTier(HDD over cloud)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Lands On The Fast Tier(HDD over cloud) | skipped | pass |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstLandsOnTheFastTier(RAM over cloud)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Lands On The Fast Tier(RAM over cloud) | skipped | pass |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstLandsOnTheFastTier(RAM over SD card)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Lands On The Fast Tier(RAM over SD card) | skipped | pass |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstLandsOnTheFastTier(SSD over HDD)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Lands On The Fast Tier(SSD over HDD) | skipped | pass |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstLandsOnTheFastTier(SSD over SD card)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Lands On The Fast Tier(SSD over SD card) | skipped | pass |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstRunsAtTheFastTiersPace(HDD over cloud)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Runs At The Fast Tiers Pace(HDD over cloud) | skipped | skipped |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstRunsAtTheFastTiersPace(RAM over cloud)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Runs At The Fast Tiers Pace(RAM over cloud) | skipped | skipped |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstRunsAtTheFastTiersPace(RAM over SD card)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Runs At The Fast Tiers Pace(RAM over SD card) | skipped | skipped |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstRunsAtTheFastTiersPace(SSD over HDD)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Runs At The Fast Tiers Pace(SSD over HDD) | skipped | skipped |
| SimulatedDevice | `Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstRunsAtTheFastTiersPace(SSD over SD card)` | Given AFast Landing Zone Over Slow Capacity , then The Burst Runs At The Fast Tiers Pace(SSD over SD card) | skipped | skipped |
| SimulatedDevice | `Tiering_WhenTheBurstDrainsToTheSlowTier_ThenEveryByteArrivesIntact(HDD over cloud)` | , when The Burst Drains To The Slow Tier , then Every Byte Arrives Intact(HDD over cloud) | pass | pass |
| SimulatedDevice | `Tiering_WhenTheBurstDrainsToTheSlowTier_ThenEveryByteArrivesIntact(RAM over cloud)` | , when The Burst Drains To The Slow Tier , then Every Byte Arrives Intact(RAM over cloud) | pass | pass |
| SimulatedDevice | `Tiering_WhenTheBurstDrainsToTheSlowTier_ThenEveryByteArrivesIntact(RAM over SD card)` | , when The Burst Drains To The Slow Tier , then Every Byte Arrives Intact(RAM over SD card) | pass | pass |
| SimulatedDevice | `Tiering_WhenTheBurstDrainsToTheSlowTier_ThenEveryByteArrivesIntact(SSD over HDD)` | , when The Burst Drains To The Slow Tier , then Every Byte Arrives Intact(SSD over HDD) | pass | pass |
| SimulatedDevice | `Tiering_WhenTheBurstDrainsToTheSlowTier_ThenEveryByteArrivesIntact(SSD over SD card)` | , when The Burst Drains To The Slow Tier , then Every Byte Arrives Intact(SSD over SD card) | pass | pass |
| SimulatedDevice | `Unmount_GivenBackgroundWorkIsStarved_ThenThePoolStillComesDownCleanly` | A pool whose background work is throttled to a crawl still unmounts cleanly, instead of having to be killed. | pass | pass |
| StorageFailureMatrix | `Failing_GivenAMemberErrorsOnEveryOperation_ThenTheHealthyCopyStillServesPromptly(RAM + RAM)` | Given AMember Errors On Every Operation , then The Healthy Copy Still Serves Promptly(RAM + RAM) | skipped | pass |
| StorageFailureMatrix | `Failing_GivenAMemberErrorsOnEveryOperation_ThenTheHealthyCopyStillServesPromptly(RAM + SD card)` | Given AMember Errors On Every Operation , then The Healthy Copy Still Serves Promptly(RAM + SD card) | skipped | pass |
| StorageFailureMatrix | `Failing_GivenAMemberErrorsOnEveryOperation_ThenTheHealthyCopyStillServesPromptly(real: temp directory + D:\)` | Given AMember Errors On Every Operation , then The Healthy Copy Still Serves Promptly(real: temp directory + D:\) | skipped | not run |
| StorageFailureMatrix | `Failing_GivenAMemberErrorsOnEveryOperation_ThenTheHealthyCopyStillServesPromptly(SSD + cloud)` | Given AMember Errors On Every Operation , then The Healthy Copy Still Serves Promptly(SSD + cloud) | skipped | pass |
| StorageFailureMatrix | `Failing_GivenAMemberErrorsOnEveryOperation_ThenTheHealthyCopyStillServesPromptly(SSD + HDD)` | Given AMember Errors On Every Operation , then The Healthy Copy Still Serves Promptly(SSD + HDD) | skipped | pass |
| StorageFailureMatrix | `PowerCut_GivenAMemberIsAlsoMissingAfterwards_ThenTheSurvivorStillServes(RAM + RAM)` | Given AMember Is Also Missing Afterwards , then The Survivor Still Serves(RAM + RAM) | pass | pass |
| StorageFailureMatrix | `PowerCut_GivenAMemberIsAlsoMissingAfterwards_ThenTheSurvivorStillServes(RAM + SD card)` | Given AMember Is Also Missing Afterwards , then The Survivor Still Serves(RAM + SD card) | pass | pass |
| StorageFailureMatrix | `PowerCut_GivenAMemberIsAlsoMissingAfterwards_ThenTheSurvivorStillServes(real: temp directory + D:\)` | Given AMember Is Also Missing Afterwards , then The Survivor Still Serves(real: temp directory + D:\) | pass | not run |
| StorageFailureMatrix | `PowerCut_GivenAMemberIsAlsoMissingAfterwards_ThenTheSurvivorStillServes(SSD + cloud)` | Given AMember Is Also Missing Afterwards , then The Survivor Still Serves(SSD + cloud) | pass | pass |
| StorageFailureMatrix | `PowerCut_GivenAMemberIsAlsoMissingAfterwards_ThenTheSurvivorStillServes(SSD + HDD)` | Given AMember Is Also Missing Afterwards , then The Survivor Still Serves(SSD + HDD) | pass | pass |
| StorageFailureMatrix | `PowerCut_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvives(RAM + RAM)` | Given Files Were Written And Closed , then Every Byte Survives(RAM + RAM) | pass | pass |
| StorageFailureMatrix | `PowerCut_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvives(RAM + SD card)` | Given Files Were Written And Closed , then Every Byte Survives(RAM + SD card) | pass | pass |
| StorageFailureMatrix | `PowerCut_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvives(real: temp directory + D:\)` | Given Files Were Written And Closed , then Every Byte Survives(real: temp directory + D:\) | pass | not run |
| StorageFailureMatrix | `PowerCut_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvives(SSD + cloud)` | Given Files Were Written And Closed , then Every Byte Survives(SSD + cloud) | pass | pass |
| StorageFailureMatrix | `PowerCut_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvives(SSD + HDD)` | Given Files Were Written And Closed , then Every Byte Survives(SSD + HDD) | pass | pass |
| StorageFailureMatrix | `Removed_GivenAMemberIsPulledAndStaysGone_ThenEveryFileIsStillServedPromptly(RAM + RAM)` | Given AMember Is Pulled And Stays Gone , then Every File Is Still Served Promptly(RAM + RAM) | pass | pass |
| StorageFailureMatrix | `Removed_GivenAMemberIsPulledAndStaysGone_ThenEveryFileIsStillServedPromptly(RAM + SD card)` | Given AMember Is Pulled And Stays Gone , then Every File Is Still Served Promptly(RAM + SD card) | pass | pass |
| StorageFailureMatrix | `Removed_GivenAMemberIsPulledAndStaysGone_ThenEveryFileIsStillServedPromptly(real: temp directory + D:\)` | Given AMember Is Pulled And Stays Gone , then Every File Is Still Served Promptly(real: temp directory + D:\) | pass | not run |
| StorageFailureMatrix | `Removed_GivenAMemberIsPulledAndStaysGone_ThenEveryFileIsStillServedPromptly(SSD + cloud)` | Given AMember Is Pulled And Stays Gone , then Every File Is Still Served Promptly(SSD + cloud) | pass | pass |
| StorageFailureMatrix | `Removed_GivenAMemberIsPulledAndStaysGone_ThenEveryFileIsStillServedPromptly(SSD + HDD)` | Given AMember Is Pulled And Stays Gone , then Every File Is Still Served Promptly(SSD + HDD) | pass | pass |
| StorageFailureMatrix | `Removed_GivenAMemberVanishesMidWrite_ThenEveryAcknowledgedByteSurvives(RAM + RAM)` | Given AMember Vanishes Mid Write , then Every Acknowledged Byte Survives(RAM + RAM) | pass | pass |
| StorageFailureMatrix | `Removed_GivenAMemberVanishesMidWrite_ThenEveryAcknowledgedByteSurvives(RAM + SD card)` | Given AMember Vanishes Mid Write , then Every Acknowledged Byte Survives(RAM + SD card) | pass | pass |
| StorageFailureMatrix | `Removed_GivenAMemberVanishesMidWrite_ThenEveryAcknowledgedByteSurvives(real: temp directory + D:\)` | Given AMember Vanishes Mid Write , then Every Acknowledged Byte Survives(real: temp directory + D:\) | pass | not run |
| StorageFailureMatrix | `Removed_GivenAMemberVanishesMidWrite_ThenEveryAcknowledgedByteSurvives(SSD + cloud)` | Given AMember Vanishes Mid Write , then Every Acknowledged Byte Survives(SSD + cloud) | pass | pass |
| StorageFailureMatrix | `Removed_GivenAMemberVanishesMidWrite_ThenEveryAcknowledgedByteSurvives(SSD + HDD)` | Given AMember Vanishes Mid Write , then Every Acknowledged Byte Survives(SSD + HDD) | pass | pass |
| StorageFailureMatrix | `Removed_GivenTheMemberReturns_ThenThePoolConvergesWithEveryCopyAgreeing(RAM + RAM)` | Given The Member Returns , then The Pool Converges With Every Copy Agreeing(RAM + RAM) | pass | pass |
| StorageFailureMatrix | `Removed_GivenTheMemberReturns_ThenThePoolConvergesWithEveryCopyAgreeing(RAM + SD card)` | Given The Member Returns , then The Pool Converges With Every Copy Agreeing(RAM + SD card) | pass | pass |
| StorageFailureMatrix | `Removed_GivenTheMemberReturns_ThenThePoolConvergesWithEveryCopyAgreeing(real: temp directory + D:\)` | Given The Member Returns , then The Pool Converges With Every Copy Agreeing(real: temp directory + D:\) | pass | not run |
| StorageFailureMatrix | `Removed_GivenTheMemberReturns_ThenThePoolConvergesWithEveryCopyAgreeing(SSD + cloud)` | Given The Member Returns , then The Pool Converges With Every Copy Agreeing(SSD + cloud) | pass | pass |
| StorageFailureMatrix | `Removed_GivenTheMemberReturns_ThenThePoolConvergesWithEveryCopyAgreeing(SSD + HDD)` | Given The Member Returns , then The Pool Converges With Every Copy Agreeing(SSD + HDD) | pass | pass |
| Tiering | `Tiering_GivenAFileHasDrained_ThenTheFastTierIsFreedAgain` | The fast tier is freed again after a file drains, so a landing zone does not fill up permanently. | pass | pass |
| Tiering | `Tiering_GivenAFileIsWritten_ThenItLandsOnTheFastTierAndDrainsToCapacity` | New data lands on the fast tier first, then the drainer moves it down to capacity storage on its own. | pass | pass |
| Tiering | `Tiering_GivenALandingZone_ThenWritesAreAcceptedAndReadBackIntact` | A landing-zone pool accepts writes and serves them back correctly through the mount. | pass | pass |
| Tiering | `Tiering_WhileTheMoverIsRelocatingFiles_ThenTheyStayReadableAndWritable` | Tiering is transparent: a file stays readable AND writable throughout, including while the mover is relocating it. | pass | pass |
| Trash | `Trash_GivenAFileWasTrashed_ThenItsNameCanBeUsedAgainAtOnce` | A trashed file's name is free again immediately: creating a new file at the same path is not confused by the deleted one. | pass | pass |
| Trash | `Trash_GivenItIsEnabled_ThenADeletedFilesBytesAreKeptIntact` | With the trash on, a deleted file leaves the pool but its bytes are kept, whole, on a member. | pass | pass |
| Trash | `Trash_GivenItIsOff_ThenADeleteIsPermanent` | With the trash off — the default — a delete really is permanent and leaves nothing behind. | pass | pass |
| WebUi | `Api_GivenTheDashboardFrame_ThenEveryMemberCarriesAResolvedState` | Given The Dashboard Frame , then Every Member Carries AResolved State | pass | pass |
| WebUi | `Assets_GivenEveryStateTheDaemonCanReport_ThenTheShippedStylesheetPaintsIt` | Given Every State The Daemon Can Report , then The Shipped Stylesheet Paints It | pass | pass |
| WebUi | `Dashboard_GivenSmartCannotBeRead_ThenStorageIsMarkedUnknownRatherThanFailing` | Given Smart Cannot Be Read , then Storage Is Marked Unknown Rather Than Failing | pass | pass |
| WebUi | `Dashboard_WhenAPoolIsPresent_ThenItsActionsAreOffered` | , when APool Is Present , then Its Actions Are Offered | pass | pass |
| WebUi | `Dashboard_WhenOpenedWithoutAToken_ThenItDoesNotLeakPoolData` | , when Opened Without AToken , then It Does Not Leak Pool Data | pass | pass |
| WebUi | `Dashboard_WhenOpenedWithTheToken_ThenItRendersThePoolWithoutScriptErrors` | , when Opened With The Token , then It Renders The Pool Without Script Errors | pass | pass |
| WebUi | `Dashboard_WhenTheLiveStreamConnects_ThenTheIndicatorReportsItAsLive` | , when The Live Stream Connects , then The Indicator Reports It As Live | pass | pass |

`skipped` marks a scenario the platform cannot express or one deliberately held back against a
known defect — the reason travels with the test, in its `Assert.Ignore`/`[Ignore]` text.
