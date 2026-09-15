using System;

namespace GrannyTAS
{
    /// <summary>
    /// One entry in the saved-macro catalog the panel lists. Built from
    /// <see cref="MacroFile.PeekHeader"/> plus filesystem metadata rather than
    /// a full <see cref="MacroFile.Load"/> — the panel has no text entry (see
    /// <see cref="TasWindow"/>'s header comment), so browsing saved runs means
    /// listing a directory, and doing that with a full frame-by-frame parse
    /// per file would make opening the list itself the slow part.
    /// </summary>
    public readonly struct SavedMacroInfo
    {
        public string Path { get; }
        public string FileName { get; }
        public int FrameCount { get; }
        public float DurationSeconds { get; }
        public string Scene { get; }
        public DateTime LastWriteTimeUtc { get; }

        public SavedMacroInfo(string path, int frameCount, float durationSeconds, string scene, DateTime lastWriteTimeUtc)
        {
            Path = path;
            FileName = System.IO.Path.GetFileName(path);
            FrameCount = frameCount;
            DurationSeconds = durationSeconds;
            Scene = scene;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }
    }
}
