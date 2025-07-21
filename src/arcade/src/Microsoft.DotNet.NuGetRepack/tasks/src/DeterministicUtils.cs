// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Compression;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Tools
{
    internal static class DeterministicUtils
    {
        internal sealed class DeterministicState
        {
            public DateTimeOffset DeterministicTimeStamp { get; }
            public string PsmdcpName { get; }

            public DeterministicState(
                DateTimeOffset deterministicTimeStamp,
                string psmdcpName)
            {
                DeterministicTimeStamp = deterministicTimeStamp;
                PsmdcpName = psmdcpName;
            }
        }

        public static DeterministicState GetDeterministicState(TaskLoggingHelper log, string packagePath)
        {
            using (var archive = new ZipArchive(File.Open(packagePath, FileMode.Open, FileAccess.Read), ZipArchiveMode.Read))
            {
                var spec = archive.Entries.First(e => NuGetUtils.IsNuSpec(e.FullName));
                var deterministicTime = spec.LastWriteTime;
                var psmdcpEntry = archive.Entries.First(e => e.FullName.EndsWith(".psmdcp"));
                var psmdcpName = psmdcpEntry.FullName;
                return new DeterministicState(deterministicTime, psmdcpName);
            }
        }

        public static void ReinstateDeterministicState(TaskLoggingHelper log, string packagePath, DeterministicState state, string[] additionalFilesForTimeStampReset)
        {
            // In deterministic mode, we need to restore as much as we can to the original values:
            // - File name of the .psmdcp file (the newly generated one is random)
            // - File name of the .psmdcp file embedded in _rels/.rels
            // - LastWriteTime on entries we modified
            //
            // These are not directly available via the Package API

            string originalPsmdcpName = state.PsmdcpName;
            string modifiedPsmdcpName = null;

            log.LogMessage(MessageImportance.High, $"ReinstateDeterministicState({packagePath})");

            using (var archive = new ZipArchive(File.Open(packagePath, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase))
                    {
                        modifiedPsmdcpName = entry.FullName;
                    }
                }
                RenameEntry(archive, modifiedPsmdcpName, originalPsmdcpName);
            }

            log.LogMessage(MessageImportance.High, $"Renamed psmdcp from {modifiedPsmdcpName} to {originalPsmdcpName})");

            using (var archive = new ZipArchive(File.Open(packagePath, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
            {
                var relsEntry = archive.GetEntry("_rels/.rels");

                string originalContent = null;
                using (var reader = new StreamReader(relsEntry.Open()))
                {
                    originalContent = reader.ReadToEnd();
                }

                var updatedContent = originalContent.Replace(modifiedPsmdcpName, originalPsmdcpName);

                using (var writer = new StreamWriter(relsEntry.Open()))
                {
                    writer.Write(updatedContent);
                }
            }

            log.LogMessage(MessageImportance.High, $"Updated _rels/.rels");

            using (var archive = new ZipArchive(File.Open(packagePath, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.LastWriteTime = state.DeterministicTimeStamp;
                    }

                    foreach (var file in additionalFilesForTimeStampReset)
                    {
                        if (entry.FullName.Equals(file, StringComparison.OrdinalIgnoreCase))
                        {
                            entry.LastWriteTime = state.DeterministicTimeStamp;
                        }
                    }
                }
            }

            log.LogMessage(MessageImportance.High, $"Done updating {packagePath} ");
        }

        private static void CopyStreamAndReplaceByLine(Stream source, Stream destination, string find, string replace)
        {
            using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(destination, Encoding.UTF8, leaveOpen: true);

            string line = null;
            while ((line = reader.ReadLine()) != null)
            {
                var replacedLine = line.Replace(find, replace);
                writer.WriteLine(replacedLine);
            }
        }

        private static void RenameEntry(ZipArchive archive, string oldName, string newName)
        {
            var oldEntry = archive.GetEntry(oldName);
            var newEntry = archive.CreateEntry(newName);

            using (var oldStream = oldEntry.Open())
            using (var newStream = newEntry.Open())
            {
                oldStream.CopyTo(newStream);
            }

            oldEntry.Delete();
        }

    }
}

