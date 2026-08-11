# End-to-end coverage

What the shipped `dbmount` binary is actually exercised against, on both targets, through a
real filesystem driver and a real browser.

**This file is generated** by `.github/workflows/scripts/e2e-matrix.ps1` from the end-to-end
`.trx` results of the Windows and Linux CI jobs. Do not edit it by hand — a hand-kept matrix
drifts the moment a test is added or starts failing, and then quietly misleads.

Generated from run: [31510586331](https://github.com/Hawkynt/DriveBenderUtility/actions/runs/31510586331).

43 scenarios — 38 passing on at least one target, 0 failing.

| Area | Scenario | What it covers | Windows | Linux |
| --- | --- | --- | :---: | :---: |
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
| ManagementApi | `Api_GivenNoToken_ThenEveryEndpointRefuses` | Given No Token , then Every Endpoint Refuses | pass | pass |
| ManagementApi | `Assets_GivenAFreshBrowser_ThenThePageAndItsScriptAndStylesAreServed` | Given AFresh Browser , then The Page And Its Script And Styles Are Served | pass | pass |
| ManagementApi | `Job_GivenAnUnknownTicket_ThenItIsReportedRatherThanHanging` | Given An Unknown Ticket , then It Is Reported Rather Than Hanging | pass | pass |
| ManagementApi | `LongOperation_GivenTheDriverInstallEndpoint_ThenItAnswersImmediatelyWithATicket` | Given The Driver Install Endpoint , then It Answers Immediately With ATicket | pass | pass |
| ManagementApi | `PoolLifecycle_GivenCreateThenForget_ThenTheDashboardReflectsBothWithoutAMount` | Given Create , then Forget , then The Dashboard Reflects Both Without AMount | pass | pass |
| ManagementApi | `Pools_GivenTheDashboardFrame_ThenItIsWellFormedAndCarriesTheJobList` | Given The Dashboard Frame , then It Is Well Formed And Carries The Job List | pass | pass |
| ManagementApi | `Prereqs_GivenThisMachine_ThenTheDriverStatusIsReportedHonestly` | Given This Machine , then The Driver Status Is Reported Honestly | pass | pass |
| ManagementApi | `Stream_GivenAConnectedClient_ThenLiveFramesArrive` | Given AConnected Client , then Live Frames Arrive | pass | pass |
| MemberLoss | `Capacity_GivenAMemberIsPulledMidWrite_ThenTheReportedSpaceNeverCountsTheLostStorage` | Given AMember Is Pulled Mid Write , then The Reported Space Never Counts The Lost Storage | pass | skipped |
| MemberLoss | `Capacity_GivenDuplicationIsOn_ThenStoringAFileCostsTwiceItsSize` | Given Duplication Is On , then Storing AFile Costs Twice Its Size | pass | skipped |
| MemberLoss | `Capacity_GivenTheMountedPool_ThenTheReportedSizeTracksTheStorageBehindIt` | Given The Mounted Pool , then The Reported Size Tracks The Storage Behind It | pass | skipped |
| MemberLoss | `Duplication_GivenTheConfiguredPool_ThenEveryFileReallyExistsTwice` | Given The Configured Pool , then Every File Really Exists Twice | pass | pass |
| MemberLoss | `Eject_GivenAFileIsDeletedWhileAMemberIsAway_ThenItDoesNotResurrectOnItsReturn` | Given AFile Is Deleted While AMember Is Away , then It Does Not Resurrect On Its Return | pass | pass |
| MemberLoss | `Eject_GivenAMemberIsAway_ThenWritesStillSucceedAndHealWhenItReturns` | Given AMember Is Away , then Writes Still Succeed And Heal , when It Returns | skipped | skipped |
| MemberLoss | `Eject_GivenAMemberIsPulled_ThenExistingFilesStayReadableFromTheSurvivor` | Given AMember Is Pulled , then Existing Files Stay Readable From The Survivor | pass | pass |
| MemberLoss | `Eject_GivenAMemberReturnsWhileIoIsInFlight_ThenNothingIsCorruptedOrStalled` | Given AMember Returns While Io Is In Flight , then Nothing Is Corrupted Or Stalled | skipped | skipped |
| MemberLoss | `Eject_GivenAMemberVanishesDuringAWrite_ThenTheDataThatWasAcknowledgedIsIntact` | Given AMember Vanishes During AWrite , then The Data That Was Acknowledged Is Intact | pass | pass |
| MemberLoss | `Eject_GivenEveryMemberIsGone_ThenOperationsFailCleanlyInsteadOfHanging` | Given Every Member Is Gone , then Operations Fail Cleanly Instead Of Hanging | pass | pass |
| SharedAccess | `Durability_GivenAnUnmountAndRemount_ThenEverythingWrittenIsStillThere` | Given An Unmount And Remount , then Everything Written Is Still There | pass | pass |
| SharedAccess | `Namespace_GivenParallelCreateRenameDelete_ThenTheDirectoryStaysConsistent` | Given Parallel Create Rename Delete , then The Directory Stays Consistent | pass | pass |
| SharedAccess | `SharedFile_GivenAppendersOnSeparateFiles_ThenEveryByteSurvives` | Given Appenders On Separate Files , then Every Byte Survives | pass | pass |
| SharedAccess | `SharedFile_GivenAReaderHoldsItOpenWhileItIsRenamed_ThenNeitherSideIsCorrupted` | Given AReader Holds It Open While It Is Renamed , then Neither Side Is Corrupted | pass | pass |
| SharedAccess | `SharedFile_GivenConcurrentReadersOnOneOpenFile_ThenEachSeesTheWholeContent` | Given Concurrent Readers On One Open File , then Each Sees The Whole Content | pass | pass |
| SharedAccess | `SharedFile_GivenWritersOwningDisjointRegionsOfOneFile_ThenNoRegionIsCorruptedByAnother` | Given Writers Owning Disjoint Regions Of One File , then No Region Is Corrupted By Another | pass | pass |
| SharedAccess | `SharedFile_GivenWritersReplacingItByRename_ThenEveryReadIsAWholeVersion` | Given Writers Replacing It By Rename , then Every Read Is AWhole Version | skipped | skipped |
| Tiering | `Tiering_GivenAFileHasDrained_ThenTheFastTierIsFreedAgain` | Given AFile Has Drained , then The Fast Tier Is Freed Again | skipped | skipped |
| Tiering | `Tiering_GivenAFileIsWritten_ThenItLandsOnTheFastTierAndDrainsToCapacity` | Given AFile Is Written , then It Lands On The Fast Tier And Drains To Capacity | skipped | skipped |
| Tiering | `Tiering_GivenALandingZone_ThenWritesAreAcceptedAndReadBackIntact` | Given ALanding Zone , then Writes Are Accepted And Read Back Intact | pass | pass |
| Tiering | `Tiering_WhileTheMoverIsRelocatingFiles_ThenTheyStayReadableAndWritable` | While The Mover Is Relocating Files , then They Stay Readable And Writable | pass | pass |
| WebUi | `Dashboard_WhenAPoolIsPresent_ThenItsActionsAreOffered` | , when APool Is Present , then Its Actions Are Offered | pass | pass |
| WebUi | `Dashboard_WhenOpenedWithoutAToken_ThenItDoesNotLeakPoolData` | , when Opened Without AToken , then It Does Not Leak Pool Data | pass | pass |
| WebUi | `Dashboard_WhenOpenedWithTheToken_ThenItRendersThePoolWithoutScriptErrors` | , when Opened With The Token , then It Renders The Pool Without Script Errors | pass | pass |
| WebUi | `Dashboard_WhenTheLiveStreamConnects_ThenTheIndicatorReportsItAsLive` | , when The Live Stream Connects , then The Indicator Reports It As Live | pass | pass |

`skipped` marks a scenario the platform cannot express or one deliberately held back against a
known defect — the reason travels with the test, in its `Assert.Ignore`/`[Ignore]` text.
