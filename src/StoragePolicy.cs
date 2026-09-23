using System;
using System.Collections.Generic;

namespace Rewind
{
    /// <summary>
    /// Medal's storage limit: once the clips folder passes max_storage_gb, the oldest clips go
    /// until it fits again. The newest clip is never touched, so a limit smaller than one clip
    /// still leaves the one just saved. Pure: decides, doesn't delete.
    /// </summary>
    internal static class StoragePolicy
    {
        public const long BytesPerGb = 1024L * 1024 * 1024;

        /// <summary>The clips to delete, oldest first, so that what remains fits under capBytes. Empty when it already fits or cap is 0.</summary>
        public static IList<ClipInfo> ToDelete(IList<ClipInfo> clips, long capBytes)
        {
            if (clips == null) throw new ArgumentNullException("clips");
            if (capBytes < 0) throw new ArgumentOutOfRangeException("capBytes");
            var doomed = new List<ClipInfo>();
            if (capBytes == 0 || clips.Count < 2) return doomed.AsReadOnly();

            var oldestFirst = new List<ClipInfo>(clips);
            oldestFirst.Sort((a, b) => a.Taken.CompareTo(b.Taken));
            long total = 0;
            foreach (var clip in oldestFirst) total += clip.Bytes;

            for (var i = 0; i < oldestFirst.Count - 1 && total > capBytes; i++)
            {
                doomed.Add(oldestFirst[i]);
                total -= oldestFirst[i].Bytes;
            }
            return doomed.AsReadOnly();
        }
    }
}
