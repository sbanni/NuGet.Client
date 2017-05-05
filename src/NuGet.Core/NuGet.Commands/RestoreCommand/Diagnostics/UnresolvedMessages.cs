// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.DependencyResolver;
using NuGet.LibraryModel;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGet.Commands
{
    /// <summary>
    /// Log errors for packages and projects that were missing.
    /// </summary>
    public static class UnresolvedMessages
    {
        /// <summary>
        /// Log errors for missing dependencies.
        /// </summary>
        public static async Task LogAsync(IEnumerable<RestoreTargetGraph> graphs, RemoteWalkContext context, ILogger logger, CancellationToken token)
        {
            var tasks = graphs.SelectMany(graph => graph.Unresolved.Select(e => GetMessageAsync(graph, e, context, logger, token))).ToArray();
            var messages = await Task.WhenAll(tasks);

            await logger.LogMessagesAsync(DiagnosticUtility.MergeOnTargetGraph(messages));
        }

        /// <summary>
        /// Create a specific error message for the unresolved dependency.
        /// </summary>
        public static async Task<RestoreLogMessage> GetMessageAsync(RestoreTargetGraph graph,
            LibraryRange unresolved,
            RemoteWalkContext context,
            ILogger logger,
            CancellationToken token)
        {
            string message = null;
            var code = NuGetLogCode.NU1100;

            if (unresolved.TypeConstraintAllows(LibraryDependencyTarget.ExternalProject)
                && !unresolved.TypeConstraintAllows(LibraryDependencyTarget.Package))
            {
                // Project
                // Check if the name is a path and if it exists. All project paths should have been normalized and converted to full paths before this.
                if (unresolved.Name.IndexOf(Path.DirectorySeparatorChar) > -1 && File.Exists(unresolved.Name))
                {
                    // File exists but the dg spec did not contain the spec
                    message = $"Unable to find project information for '{unresolved.Name}'. The project file may be invalid or missing targets required for restore.";
                    code = NuGetLogCode.NU1105;
                }
                else
                {
                    // Generic missing project error
                    message = $"Unable to find project '{unresolved.Name}'. Check that the project reference is valid and that the project file exists.";
                    code = NuGetLogCode.NU1104;
                }
            }
            else if (unresolved.TypeConstraintAllows(LibraryDependencyTarget.Package)
                        && context.RemoteLibraryProviders.Count > 0)
            {
                // Package
                var range = unresolved.VersionRange ?? VersionRange.All;
                var sourceInfo = await GetSourceInfosForIdAsync(unresolved.Name, range, context, logger, token);
                var allVersions = new SortedSet<NuGetVersion>(sourceInfo.SelectMany(e => e.Value));

                if (allVersions.Count == 0)
                {
                    // No versions found
                    code = NuGetLogCode.NU1101;
                    var sourceList = string.Join(", ",
                                        sourceInfo.Select(e => e.Key.Source)
                                                  .OrderBy(e => e, StringComparer.OrdinalIgnoreCase));

                    message = $"Unable to find package {unresolved.Name}. No packages exist with this id in source(s): {sourceList}";
                }
                else
                {
                    // At least one version found
                    var firstLine = string.Empty;
                    var rangeString = range.ToNonSnapshotRange().PrettyPrint();

                    if (!IsPrereleaseAllowed(range) && HasPrereleaseVersionsOnly(range, allVersions))
                    {
                        code = NuGetLogCode.NU1102;
                        firstLine = $"Unable to find package {unresolved.Name} with version {rangeString}";
                    }
                    else
                    {
                        code = NuGetLogCode.NU1103;
                        firstLine = $"Unable to find a stable package {unresolved.Name} with version {rangeString}";
                    }

                    var lines = new List<string>()
                    {
                        firstLine
                    };

                    lines.AddRange(sourceInfo.Select(e => FormatSourceInfo(e, range)));

                    message = DiagnosticUtility.GetMultiLineMessage(lines);
                }
            }
            else
            {
                // Unknown or non-specific.
                // Also shown when no sources exist.
                message = string.Format(CultureInfo.CurrentCulture,
                    Strings.Log_UnresolvedDependency,
                    unresolved.ToString(),
                    graph.Name);

                code = NuGetLogCode.NU1100;
            }

            return RestoreLogMessage.CreateError(code, message);
        }

        /// <summary>
        /// True if no stable versions satisfy the range 
        /// but a pre-release version is found.
        /// </summary>
        public static bool HasPrereleaseVersionsOnly(VersionRange range, IEnumerable<NuGetVersion> versions)
        {
            return (versions.Any(e => e.IsPrerelease && range.Satisfies(e))
                && !versions.Any(e => !e.IsPrerelease && range.Satisfies(e)));
        }

        /// <summary>
        /// True if the range allows pre-release versions.
        /// </summary>
        public static bool IsPrereleaseAllowed(VersionRange range)
        {
            return (range?.MaxVersion?.IsPrerelease == true
                || range?.MinVersion?.IsPrerelease == true);
        }

        /// <summary>
        /// Found 2839 version(s) in http://dotnet.myget.org/F/nuget-build/  [ Nearest version: 1.0.0-beta ]
        /// </summary>
        public static string FormatSourceInfo(KeyValuePair<PackageSource, SortedSet<NuGetVersion>> sourceInfo, VersionRange range)
        {
            var bestMatch = GetBestMatch(sourceInfo.Value, range);
            var bestMatchString = bestMatch == null ? string.Empty : $" [ Nearest version: {bestMatch.ToNormalizedString()} ]";

            return $"Found {sourceInfo.Value.Count} version(s) in {sourceInfo.Key.Source}{bestMatchString}";
        }

        /// <summary>
        /// Get the complete set of source info for a package id.
        /// </summary>
        public static async Task<List<KeyValuePair<PackageSource, SortedSet<NuGetVersion>>>> GetSourceInfosForIdAsync(
            string id,
            VersionRange range,
            RemoteWalkContext context,
            ILogger logger,
            CancellationToken token)
        {
            var sources = new List<KeyValuePair<PackageSource, SortedSet<NuGetVersion>>>();

            // Get versions from all sources. These should be cached by the providers already.
            var tasks = context.RemoteLibraryProviders
                .Select(e => GetSourceInfoForIdAsync(e, id, context.CacheContext, logger, token))
                .ToArray();

            foreach (var task in tasks)
            {
                sources.Add(await task);
            }

            // Sort by most package versions, then by source path.
            return sources.OrderByDescending(e => e.Value.Count)
                .ThenBy(e => e.Key.Source, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Find all package versions from a source.
        /// </summary>
        public static async Task<KeyValuePair<PackageSource, SortedSet<NuGetVersion>>> GetSourceInfoForIdAsync(
            IRemoteDependencyProvider provider,
            string id,
            SourceCacheContext cacheContext,
            ILogger logger,
            CancellationToken token)
        {
            // Find all versions from a source.
            var versions = await provider.GetAllVersionsAsync(id, cacheContext, logger, token);

            return new KeyValuePair<PackageSource, SortedSet<NuGetVersion>>(
                provider.Source,
                new SortedSet<NuGetVersion>(versions));
        }

        /// <summary>
        /// Find the best match on the feed.
        /// </summary>
        public static NuGetVersion GetBestMatch(SortedSet<NuGetVersion> versions, VersionRange range)
        {
            if (versions.Count == 0)
            {
                return null;
            }

            // Find a pivot point
            var ideal = new NuGetVersion(0, 0, 0);

            if (range != null)
            {
                if (range.HasUpperBound)
                {
                    ideal = range.MaxVersion;
                }

                if (range.HasLowerBound)
                {
                    ideal = range.MinVersion;
                }
            }

            // Take the lowest version higher than the pivot if one exists.
            var bestMatch = versions.Where(e => e >= ideal).FirstOrDefault();

            if (bestMatch == null)
            {
                // Take the highest possible version.
                bestMatch = versions.Last();
            }

            return bestMatch;
        }
    }
}
