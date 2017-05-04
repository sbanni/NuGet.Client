﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;
using NuGet.LibraryModel;
using NuGet.Versioning;

namespace NuGet.Commands
{
    /// <summary>
    /// Warning and error logging helpers.
    /// </summary>
    public static class DiagnosticUtility
    {
        /// <summary>
        /// Format an id and include the version only if it exists.
        /// Ignore versions for projects.
        /// </summary>
        public static string FormatIdentity(LibraryIdentity identity)
        {
            // Display the version if it exists
            // Ignore versions for projects
            if (identity.Version != null && identity.Type == LibraryType.Package)
            {
                return $"{identity.Name} {identity.Version.ToNormalizedString()}";
            }

            return identity.Name;
        }

        /// <summary>
        /// Format an id and include the range only if it has bounds.
        /// </summary>
        public static string FormatDependency(string id, VersionRange range)
        {
            if (range == null || !(range.HasLowerBound || range.HasUpperBound))
            {
                return id;
            }

            return $"{id} {range.ToNonSnapshotRange().PrettyPrint()}";
        }

        /// <summary>
        /// Format an id and include the lower bound only if it has one.
        /// </summary>
        public static string FormatExpectedIdentity(string id, VersionRange range)
        {
            if (range == null || !range.HasLowerBound || !range.IsMinInclusive)
            {
                return id;
            }

            return $"{id} {range.MinVersion.ToNormalizedString()}";
        }

        /// <summary>
        /// Format a graph name with an optional RID.
        /// </summary>
        public static string FormatGraphName(RestoreTargetGraph graph)
        {
            if (string.IsNullOrEmpty(graph.RuntimeIdentifier))
            {
                return $"({graph.Framework.DotNetFrameworkName})";
            }
            else
            {
                return $"({graph.Framework.DotNetFrameworkName} RuntimeIdentifier: {graph.RuntimeIdentifier})";
            }
        }

        /// <summary>
        /// Format a message as:
        /// 
        /// First line
        ///   - second
        ///   - third
        /// </summary>
        public static string GetMultiLineMessage(IEnumerable<string> lines)
        {
            var sb = new StringBuilder();

            foreach (var line in lines)
            {
                if (sb.Length == 0)
                {
                    sb.Append(line);
                }
                else
                {
                    sb.Append(Environment.NewLine);
                    sb.Append($"  - {line}");
                }
            }

            return sb.ToString();
        }
    }
}
