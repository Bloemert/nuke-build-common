﻿// Copyright 2018 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nuke.Platform;

namespace Nuke.Common.Execution
{
    internal static class GraphService
    {
        public static void ShowGraph<T>(T build)
            where T : NukeBuild
        {
            string GetStringFromStream(Stream stream)
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }

            var assembly = typeof(BuildExecutor).GetTypeInfo().Assembly;
            var resourceName = typeof(BuildExecutor).Namespace + ".graph.html";
            var resourceStream = assembly.GetManifestResourceStream(resourceName).NotNull("resourceStream != null");

            var graph = new StringBuilder();
            foreach (var target in build.TargetDefinitions)
            {
                var dependentBy = build.TargetDefinitions.Where(x => x.TargetDefinitionDependencies.Contains(target)).ToList();
                if (dependentBy.Count == 0)
                    graph.AppendLine(target.GetDeclaration());
                else
                    dependentBy.ForEach(x => graph.AppendLine($"{target.GetDeclaration()} --> {x.GetDeclaration()}"));
            }

            var path = Path.Combine(NukeBuild.TemporaryDirectory, "graph.html");
            var contents = GetStringFromStream(resourceStream).Replace("__GRAPH__", graph.ToString());
            File.WriteAllText(path, contents);

            // Workaround for https://github.com/dotnet/corefx/issues/10361
            Process.Start(new ProcessStartInfo
                          {
                              FileName = path,
                              UseShellExecute = true
                          });
        }

        private static string GetDeclaration(this TargetDefinition targetDefinition)
        {
            return targetDefinition.IsDefault
                ? $"defaultTarget[{targetDefinition.Name}]"
                : targetDefinition.Name;
        }
    }
}
