# End-to-end coverage

What the shipped `dbmount` binary is actually exercised against, on both targets, through a
real filesystem driver and a real browser.

**This file is generated** by `.github/workflows/scripts/e2e-matrix.ps1` from the end-to-end
`.trx` results of the Windows and Linux CI jobs. Do not edit it by hand — a hand-kept matrix
drifts the moment a test is added or starts failing, and then quietly misleads.

Generated from run: [31512619045](https://github.com/Hawkynt/DriveBenderUtility/actions/runs/31512619045).

57 scenarios — 52 passing on at least one target, 2 failing.

| Area | Scenario | What it covers | Windows | Linux |
| --- | --- | --- | :---: | :---: |
| Boundary | `Create_GivenManyThreadsRaceForOnePath_ThenExactlyOneFileExistsAndItIsWhole` | Many threads creating the same new path at once: one wins, no exception escapes unexplained, and the file is whole. | pass | pass |
| Boundary | `Names_GivenLongPathsAndAwkwardNames_ThenTheyRemainAddressableAfterARemount` | A deep tree with long paths and awkward but legal names survives a remount with its content addressable. | pass | pass |
| Boundary | `Names_GivenTwoPathsDifferingOnlyInCase_ThenNoContentIsLost` | Two names differing only in case: on Windows they are one file, on Linux two, and in neither case does content go missing. | pass | **FAIL** |
| Boundary | `Reads_GivenTheyStraddleBlockBoundaries_ThenTheyReturnTheRightBytes` | Reads that straddle a page or block boundary return the right bytes, whatever offset and length they use. | pass | pass |
| Boundary | `Sizes_GivenFilesOnTheCacheAndBlockBoundaries_ThenEachRoundTripsExactly` | Files sized exactly on and either side of the page, buffer and block boundaries round-trip byte for byte. | pass | pass |
| Boundary | `Sparse_GivenAWriteFarBeyondTheEnd_ThenTheHoleReadsAsZeroes` | Writing far past the end of a file leaves a hole that reads as zeroes, and the bytes written land at the right offset. | pass | pass |
| Boundary | `Truncate_GivenAFileIsShrunkThenGrown_ThenTheOldContentDoesNotResurface` | A file shrunk and grown again reads as zeroes in the re-exposed region, never the content that used to be there. | pass | pass |
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
| Durability | `Divergence_GivenEachMemberTookAWriteWhileTheOtherWasAway_ThenOneWholeVersionIsServed` | Each member took a write while the other was away: the pool serves one whole version, never a mixture of the two. | pass | **FAIL** |
| ManagementApi | `Api_GivenNoToken_ThenEveryEndpointRefuses` | Given No Token , then Every Endpoint Refuses | pass | pass |
| ManagementApi | `Assets_GivenAFreshBrowser_ThenThePageAndItsScriptAndStylesAreServed` | Given AFresh Browser , then The Page And Its Script And Styles Are Served | pass | pass |
| ManagementApi | `Job_GivenAnUnknownTicket_ThenItIsReportedRatherThanHanging` | Given An Unknown Ticket , then It Is Reported Rather Than Hanging | pass | pass |
| ManagementApi | `LongOperation_GivenTheDriverInstallEndpoint_ThenItAnswersImmediatelyWithATicket` | Given The Driver Install Endpoint , then It Answers Immediately With ATicket | pass | pass |
| ManagementApi | `PoolLifecycle_GivenCreateThenForget_ThenTheDashboardReflectsBothWithoutAMount` | Given Create , then Forget , then The Dashboard Reflects Both Without AMount | pass | pass |
| ManagementApi | `Pools_GivenTheDashboardFrame_ThenItIsWellFormedAndCarriesTheJobList` | Given The Dashboard Frame , then It Is Well Formed And Carries The Job List | pass | pass |
| ManagementApi | `Prereqs_GivenThisMachine_ThenTheDriverStatusIsReportedHonestly` | Given This Machine , then The Driver Status Is Reported Honestly | pass | pass |
| ManagementApi | `Stream_GivenAConnectedClient_ThenLiveFramesArrive` | Given AConnected Client , then Live Frames Arrive | pass | pass |
| MemberLoss | `Capacity_GivenAMemberIsPulledMidWrite_ThenTheReportedSpaceNeverCountsTheLostStorage` | A member pulled mid-write: the pool reports less free space afterwards and never claims the lost disk's capacity. | pass | skipped |
| MemberLoss | `Capacity_GivenDuplicationIsOn_ThenStoringAFileCostsTwiceItsSize` | Duplication charges twice: storing N bytes with two copies consumes about 2N of the pool's free space. | pass | skipped |
| MemberLoss | `Capacity_GivenTheMountedPool_ThenTheReportedSizeTracksTheStorageBehindIt` | Given The Mounted Pool , then The Reported Size Tracks The Storage Behind It | pass | skipped |
| MemberLoss | `Duplication_GivenTheConfiguredPool_ThenEveryFileReallyExistsTwice` | Given The Configured Pool , then Every File Really Exists Twice | pass | pass |
| MemberLoss | `Eject_GivenAFileIsDeletedWhileAMemberIsAway_ThenItDoesNotResurrectOnItsReturn` | Given AFile Is Deleted While AMember Is Away , then It Does Not Resurrect On Its Return | pass | pass |
| MemberLoss | `Eject_GivenAMemberIsAway_ThenWritesStillSucceedAndHealWhenItReturns` | Given AMember Is Away , then Writes Still Succeed And Heal , when It Returns _(held back: Fails against a real gap, not a flaky one: a file written while a member was away is NOT healed back to its duplication level when that member returns - waited two minutes with the background pump running. The degraded write itself succeeds, and a file deleted while the member was away correctly stays deleted, so tombstone replay works while owed-copy heal does not. See docs/Issues.md 'Still open'.)_ | skipped | skipped |
| MemberLoss | `Eject_GivenAMemberIsPulled_ThenExistingFilesStayReadableFromTheSurvivor` | Given AMember Is Pulled , then Existing Files Stay Readable From The Survivor | pass | pass |
| MemberLoss | `Eject_GivenAMemberReturnsWhileIoIsInFlight_ThenNothingIsCorruptedOrStalled` | Given AMember Returns While Io Is In Flight , then Nothing Is Corrupted Or Stalled _(held back: Fails while a member is reattached under live traffic. Related to the un-run owed-copy heal recorded in docs/Issues.md; held back rather than weakened so the scenario is not lost.)_ | skipped | skipped |
| MemberLoss | `Eject_GivenAMemberVanishesDuringAWrite_ThenTheDataThatWasAcknowledgedIsIntact` | Given AMember Vanishes During AWrite , then The Data That Was Acknowledged Is Intact | pass | pass |
| MemberLoss | `Eject_GivenEveryMemberIsGone_ThenOperationsFailCleanlyInsteadOfHanging` | Given Every Member Is Gone , then Operations Fail Cleanly Instead Of Hanging | pass | pass |
| SharedAccess | `Durability_GivenAnUnmountAndRemount_ThenEverythingWrittenIsStillThere` | Given An Unmount And Remount , then Everything Written Is Still There | pass | pass |
| SharedAccess | `Namespace_GivenParallelCreateRenameDelete_ThenTheDirectoryStaysConsistent` | Given Parallel Create Rename Delete , then The Directory Stays Consistent | pass | pass |
| SharedAccess | `SharedFile_GivenAppendersOnSeparateFiles_ThenEveryByteSurvives` | Given Appenders On Separate Files , then Every Byte Survives | pass | pass |
| SharedAccess | `SharedFile_GivenAReaderHoldsItOpenWhileItIsRenamed_ThenNeitherSideIsCorrupted` | Given AReader Holds It Open While It Is Renamed , then Neither Side Is Corrupted | pass | pass |
| SharedAccess | `SharedFile_GivenConcurrentReadersOnOneOpenFile_ThenEachSeesTheWholeContent` | Given Concurrent Readers On One Open File , then Each Sees The Whole Content | pass | pass |
| SharedAccess | `SharedFile_GivenWritersOwningDisjointRegionsOfOneFile_ThenNoRegionIsCorruptedByAnother` | Given Writers Owning Disjoint Regions Of One File , then No Region Is Corrupted By Another | pass | pass |
| SharedAccess | `SharedFile_GivenWritersReplacingItByRename_ThenEveryReadIsAWholeVersion` | Given Writers Replacing It By Rename , then Every Read Is AWhole Version _(held back: Fails against a REAL defect, not a flaky one: after a file is replaced by rename, readers that open the name afresh keep seeing the OLD content. Measured on Windows: 6 replacements landed and the file settled on version 40, while all 3200 reads taken during the run returned version 1. Left in place, and ignored rather than weakened, so the scenario is not lost - see docs/Issues.md 'Still open'.)_ | skipped | skipped |
| Tiering | `Tiering_GivenAFileHasDrained_ThenTheFastTierIsFreedAgain` | The fast tier is freed again after a file drains, so a landing zone does not fill up permanently. _(held back: Same observation as the drain test above: the fast tier is never freed because the file never leaves it. See docs/Issues.md.)_ | skipped | skipped |
| Tiering | `Tiering_GivenAFileIsWritten_ThenItLandsOnTheFastTierAndDrainsToCapacity` | New data lands on the fast tier first, then the drainer moves it down to capacity storage on its own. _(held back: Observed: a file written to a pool with a landing-zone member is still not present on the capacity member two minutes later, with the background pump running. Needs confirming whether the drainer never ran or the landing role was not applied to the member. See docs/Issues.md.)_ | skipped | skipped |
| Tiering | `Tiering_GivenALandingZone_ThenWritesAreAcceptedAndReadBackIntact` | A landing-zone pool accepts writes and serves them back correctly through the mount. | pass | pass |
| Tiering | `Tiering_WhileTheMoverIsRelocatingFiles_ThenTheyStayReadableAndWritable` | Tiering is transparent: a file stays readable AND writable throughout, including while the mover is relocating it. | pass | pass |
| WebUi | `Dashboard_WhenAPoolIsPresent_ThenItsActionsAreOffered` | , when APool Is Present , then Its Actions Are Offered | pass | pass |
| WebUi | `Dashboard_WhenOpenedWithoutAToken_ThenItDoesNotLeakPoolData` | , when Opened Without AToken , then It Does Not Leak Pool Data | pass | pass |
| WebUi | `Dashboard_WhenOpenedWithTheToken_ThenItRendersThePoolWithoutScriptErrors` | , when Opened With The Token , then It Renders The Pool Without Script Errors | pass | pass |
| WebUi | `Dashboard_WhenTheLiveStreamConnects_ThenTheIndicatorReportsItAsLive` | , when The Live Stream Connects , then The Indicator Reports It As Live | pass | pass |

`skipped` marks a scenario the platform cannot express or one deliberately held back against a
known defect — the reason travels with the test, in its `Assert.Ignore`/`[Ignore]` text.
