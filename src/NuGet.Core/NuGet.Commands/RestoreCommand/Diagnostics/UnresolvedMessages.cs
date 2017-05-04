// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.DependencyResolver;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Shared;
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
        public static async Task LogAsync(RestoreTargetGraph graph, RemoteWalkContext context, ILogger logger, CancellationToken token)
        {
            var tasks = graph.Unresolved.Select(e => GetMessageAsync(graph, e, context, logger, token));
            var messages = await Task.WhenAll(tasks);
            await logger.LogMessagesAsync(messages);
        }
        
        /// <summary>
        /// Create a specific error message for the unresolved dependency.
        /// </summary>
        public static async Task<RestoreLogMessage> GetMessageAsync(RestoreTargetGraph graph, LibraryRange unresolved, RemoteWalkContext context, ILogger logger, CancellationToken token)
        {
            string message = null;
            var code = NuGetLogCode.NU1100;

            if (unresolved.TypeConstraintAllows(LibraryDependencyTarget.ExternalProject)
                && !unresolved.TypeConstraintAllows(LibraryDependencyTarget.Package))
            {
                // Project



            }
            else if (unresolved.TypeConstraintAllows(LibraryDependencyTarget.Package))
            {
                // Package
                var range = unresolved.VersionRange ?? VersionRange.All;
                var sourceInfo = await GetSourceInfoForIdAsync(unresolved.Name, range, context, logger, token);

                if (sourceInfo.All(e => e.Item2 == 0))
                {
                    // No versions found
                    var sourceList = string.Join(", ", sourceInfo.Select(e => e.Item1.Source));
                    message = $"Unable to find package {unresolved.Name}. No packages exist with this id in source(s): {sourceList}";
                    code = NuGetLogCode.NU1101;
                }
                else
                {
                    // At least one version found
                    var lines = new List<string>()
                    {
                        $"Unable to find package {unresolved.Name} with version {range.ToNonSnapshotRange().PrettyPrint()}"
                    };

                    lines.AddRange(sourceInfo.Select(FormatSourceInfo));

                    message = DiagnosticUtility.GetMultiLineMessage(lines);
                    code = NuGetLogCode.NU1102;
                }
            }
            else
            {
                // Unknown or non-specific.
                var packageDisplayName = DiagnosticUtility.FormatDependency(unresolved.Name, unresolved.VersionRange);

                message = string.Format(CultureInfo.CurrentCulture,
                    Strings.Log_UnresolvedDependency,
                    packageDisplayName,
                    graph.Name);

                code = NuGetLogCode.NU1100;
            }

            return RestoreLogMessage.CreateError(code, message); ;
        }

        /// <summary>
        /// Found 2839 version(s) in http://dotnet.myget.org/F/nuget-build/  [ Nearest version: 1.0.0-beta ]
        /// </summary>
        public static string FormatSourceInfo(Tuple<PackageSource, int, NuGetVersion> sourceInfo)
        {
            var nearest = sourceInfo.Item3 == null ? string.Empty : $" [ Nearest version: {sourceInfo.Item3.ToNormalizedString()} ]";

            return $"Found {sourceInfo.Item2} version(s) in {sourceInfo.Item1.Source}{nearest}";
        }

        public static async Task<List<Tuple<PackageSource, int, NuGetVersion>>> GetSourceInfoForIdAsync(
            string id,
            VersionRange range,
            RemoteWalkContext context,
            ILogger logger,
            CancellationToken token)
        {
            var sources = new List<Tuple<PackageSource, int, NuGetVersion>>();

            // Get versions from all sources. These should be cached by the providers already.
            var tasks = context.RemoteLibraryProviders
                .Select(e => GetSourceInfoForIdAsync(e, id, range, context, logger, token))
                .ToArray();

            foreach (var task in tasks)
            {
                sources.Add(await task);
            }

            return sources.OrderByDescending(e => e.Item2).ThenBy(e => e.Item1.Source).ToList();
        }


        public static async Task<Tuple<PackageSource, int, NuGetVersion>> GetSourceInfoForIdAsync(
            IRemoteDependencyProvider provider,
            string id,
            VersionRange range,
            RemoteWalkContext context,
            ILogger logger,
            CancellationToken token)
        {
            var versions = (await provider.GetAllVersionsAsync(id, context.CacheContext, logger, token)).AsList();

            var bestMatch = GetBestMatch(versions, range);

            return new Tuple<PackageSource, int, NuGetVersion>(provider.Source, versions.Count, bestMatch);
        }

        /// <summary>
        /// Find the best match on the feed.
        /// </summary>
        public static NuGetVersion GetBestMatch(IEnumerable<NuGetVersion> versions, VersionRange range)
        {
            // Sort lowest to highest.
            var sorted = versions.OrderBy(e => e).ToList();

            if (sorted.Count == 0)
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
            var bestMatch = sorted.Where(e => e >= ideal).FirstOrDefault();

            if (bestMatch == null)
            {
                // Take the highest possible version.
                bestMatch = sorted.Last();
            }

            return bestMatch;
        }
    }
}
