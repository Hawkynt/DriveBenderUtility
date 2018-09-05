using System;
using System.Collections.Generic;
using System.Linq;
using Libraries;

namespace DriveBender {
  partial class Pool {
    /// <summary>
    /// Rebalances files on pool to ensure a good average across all drives.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public void Rebalance(Action<string> logger) {
      var pool = this;

      logger?.Invoke($"Pool {pool.Name}({pool.Description})");

      var drives = pool.Drives.ToArray();
      var drivesWithSpaceFree = drives.ToDictionary(d => d, d => d.BytesFree);

      foreach (var drive in drives.OrderBy(i => i.Name))
        logger?.Invoke(
          $@" + Drive {drive.Name} {drive.BytesUsed * 100f / drive.BytesTotal:0.#}% ({
            FilesizeFormatter.FormatIEC(drive.BytesUsed, "0.#")} used, {
            FilesizeFormatter.FormatIEC(drive.BytesFree, "0.#")} free, {
            FilesizeFormatter.FormatIEC(drive.BytesTotal, "0.#")} total)");

      var avgBytesFree = drives.Sum(i => drivesWithSpaceFree[i]) / (ulong)drives.Length;
      logger?.Invoke($@" * Average free {FilesizeFormatter.FormatIEC(avgBytesFree, "0.#")}");

      const ulong MIN_BYTES_DIFFERENCE_BEFORE_ACTING = 2 * 1024 * 1024UL;
      logger?.Invoke(
        $@" * Difference per drive before balancing {
          FilesizeFormatter.FormatIEC(MIN_BYTES_DIFFERENCE_BEFORE_ACTING, "0.#")}");

      if (avgBytesFree < MIN_BYTES_DIFFERENCE_BEFORE_ACTING)
        return;

      var valueBeforeGettingDataFrom = avgBytesFree - MIN_BYTES_DIFFERENCE_BEFORE_ACTING;
      var valueBeforePuttingDataTo = avgBytesFree + MIN_BYTES_DIFFERENCE_BEFORE_ACTING;

      while (_DoRebalanceRun(
        drives,
        drivesWithSpaceFree,
        valueBeforeGettingDataFrom,
        valueBeforePuttingDataTo,
        logger,
        avgBytesFree)) {
        ;
      }

    }

    private static bool _DoRebalanceRun(
      IPoolDrive[] drives,
      IDictionary<IPoolDrive, ulong> drivesWithSpaceFree,
      ulong valueBeforeGettingDataFrom,
      ulong valueBeforePuttingDataTo,
      Action<string> logger,
      ulong avgBytesFree
      ) {
      var drivesToGetFilesFrom = drives.Where(i => drivesWithSpaceFree[i] < valueBeforeGettingDataFrom).ToArray();
      var drivesToPutFilesTo = drives.Where(i => drivesWithSpaceFree[i] > valueBeforePuttingDataTo).ToArray();

      if (!(drivesToPutFilesTo.Any() && drivesToGetFilesFrom.Any()))
        return false;

      logger($@" * Drives overfilled {string.Join(", ", drivesToGetFilesFrom.Select(i => i.Name))}");
      logger($@" * Drives underfilled {string.Join(", ", drivesToPutFilesTo.Select(i => i.Name))}");

      var movedAtLeastOneFile = false;
      foreach (var sourceDrive in drivesToGetFilesFrom) {
        // get all files which could be moved somewhere else
        var files =
          sourceDrive
            .Items
            .EnumerateFiles(true)
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
            logger($@" # No more files available to move");
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
          logger(
            $@" - Moving file {fileToMove.FullName} from {sourceDrive.Name} to {targetDrive.Name}, {
              FilesizeFormatter.FormatIEC(fileSize, "0.#")}");
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