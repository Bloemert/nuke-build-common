// Copyright 2018 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Nuke.Platform.Utilities;

namespace Nuke.Common.Execution
{
    internal static class BuildExtensions
    {
        public static IReadOnlyCollection<TargetDefinition> GetTargetDefinitions<T>(
            this T build,
            Expression<Func<T, Target>> defaultTargetExpression)
            where T : NukeBuild
        {
            var defaultTarget = defaultTargetExpression.Compile().Invoke(build);
            var targetDefinitions = build.GetType()
                .GetProperties(ReflectionService.Instance)
                .Where(x => x.PropertyType == typeof(Target))
                .Select(x => LoadTargetDefinition(build, x)).ToList();
            var factoryDictionary = targetDefinitions.ToDictionary(x => x.Factory, x => x);

            foreach (var targetDefinition in targetDefinitions)
            {
                targetDefinition.IsDefault = targetDefinition.Factory == defaultTarget;
                
                var dependencies = GetDependencies(targetDefinition, factoryDictionary);
                targetDefinition.TargetDefinitionDependencies.AddRange(dependencies);
            }

            return targetDefinitions;
        }

        private static TargetDefinition LoadTargetDefinition(NukeBuild build, PropertyInfo property)
        {
            var targetFactory = (Target) property.GetValue(build);
            return TargetDefinition.Create(property.Name, targetFactory);
        }

        private static IEnumerable<TargetDefinition> GetDependencies(
            TargetDefinition targetDefinition,
            IReadOnlyDictionary<Target, TargetDefinition> factoryDictionary)
        {
            foreach (var target in targetDefinition.FactoryDependencies.Select(x => factoryDictionary[x]))
                yield return target;

            foreach (var target in targetDefinition.RunAfterTargets.Select(x => factoryDictionary[x]))
                yield return target;

            foreach (var target in factoryDictionary.Values.Where(x => x.RunBeforeTargets.Contains(targetDefinition.Factory)))
                yield return target;
        }
    }
}
