using System;
using System.Collections.Generic;
using System.Linq;

namespace DivisonM {
  partial class DriveBender {
    partial class MountPoint {
      /// <summary>
      /// Rebalances files on pool to ensure a good average across all drives.
      /// </summary>
      public void Rebalance() {
        var mountPoint = this;

        Logger($"Pool {mountPoint.Name}({mountPoint.Description})");

        var drives = mountPoint.Volumes.ToArray();
        var drivesWithSpaceFree = drives.ToDictionary(d => d, d => d.BytesFree);

        foreach (var drive in drives.OrderBy(i => i.Name))
          Logger(
            $@" + Drive {drive.Name} {drive.BytesUsed * 100f / drive.BytesTotal:0.#}% ({
                SizeFormatter.Format(drive.BytesUsed)} used, {
                SizeFormatter.Format(drive.BytesFree)} free, {
                SizeFormatter.Format(drive.BytesTotal)} total)");

        var avgBytesFree = drives.Sum(i => drivesWithSpaceFree[i]) / (ulong) drives.Length;
        Logger($" * Average free {SizeFormatter.Format(avgBytesFree)}");

        const ulong MIN_BYTES_DIFFERENCE_BEFORE_ACTING = 2 * 1024 * 1024UL;
        Logger($" * Difference per drive before balancing {SizeFormatter.Format(MIN_BYTES_DIFFERENCE_BEFORE_ACTING)}");

        if (avgBytesFree < MIN_BYTES_DIFFERENCE_BEFORE_ACTING)
          return;

        var valueBeforeGettingDataFrom = avgBytesFree - MIN_BYTES_DIFFERENCE_BEFORE_ACTING;
        var valueBeforePuttingDataTo = avgBytesFree + MIN_BYTES_DIFFERENCE_BEFORE_ACTING;

        while (_DoRebalanceRun(
          drives,
          drivesWithSpaceFree,
          valueBeforeGettingDataFrom,
          valueBeforePuttingDataTo,
          avgBytesFree)) {
          ;
        }

      }

      private static bool _DoRebalanceRun(
        IVolume[] drives,
        IDictionary<IVolume, ulong> drivesWithSpaceFree,
        ulong valueBeforeGettingDataFrom,
        ulong valueBeforePuttingDataTo,
        ulong avgBytesFree
      ) {
        var drivesToGetFilesFrom = drives.Where(i => drivesWithSpaceFree[i] < valueBeforeGettingDataFrom).ToArray();
        var drivesToPutFilesTo = drives.Where(i => drivesWithSpaceFree[i] > valueBeforePuttingDataTo).ToArray();

        if (!(drivesToPutFilesTo.Any() && drivesToGetFilesFrom.Any()))
          return false;

        Logger($@" * Drives overfilled {string.Join(", ", drivesToGetFilesFrom.Select(i => i.Name))}");
        Logger($@" * Drives underfilled {string.Join(", ", drivesToPutFilesTo.Select(i => i.Name))}");

        var movedAtLeastOneFile = false;
        foreach (var sourceDrive in drivesToGetFilesFrom) {
          // get all files which could be moved somewhere else
          var files =
              sourceDrive
                .Items
                .EnumerateFiles(true)
                .Where(t=>t.Size>=4096) /* minimum file size before moving file */
                .OrderByDescending(t => t.Size)
                .ToList()
            ;

          // as long as the source drive has less than the calculated average bytes free
          while (drivesWithSpaceFree[sourceDrive] < avgBytesFree) {
            // calculate how many bytes are left until the average is reached
            var bestFit = avgBytesFree - drivesWithSpaceFree[sourceDrive];

            // find the first file, that is nearly big enough
            var fileToMove = files.FirstOrDefault(f => f.Size <= bestFit);
            if (fileToMove == null) {
              Logger($" # No more files available to move");
              return movedAtLeastOneFile; /* no file found to move */
            }

            var fileSize = fileToMove.Size;

            // avoid to move file again
            files.Remove(fileToMove);

            // find a drive to put the file onto (basically it should not be already there and the drive should have enough free bytes available)
            var targetDrive =
              drivesToPutFilesTo.FirstOrDefault(d => drivesWithSpaceFree[d] > fileSize && !fileToMove.ExistsOnDrive(d));
            if (targetDrive == null) {
              //logger($@" # Trying to move file {fileToMove.FullName} but it is already present allowed target drive");
              continue; /* no target drive big enough */
            }

            // move file to target drive
            Logger($" - Moving file {fileToMove.FullName} from {sourceDrive.Name} to {targetDrive.Name}, {SizeFormatter.Format(fileSize)}");
            fileToMove.MoveToDrive(targetDrive);

            drivesWithSpaceFree[targetDrive] -= fileSize;
            drivesWithSpaceFree[sourceDrive] += fileSize;
            movedAtLeastOneFile = true;
          }
        } /* next overloaded drive */

        return movedAtLeastOneFile;
      }

    }
  }
}