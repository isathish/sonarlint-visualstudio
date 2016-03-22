﻿//-----------------------------------------------------------------------
// <copyright file="RuleSetInspector.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.CodeAnalysis.Extensibility;
using Microsoft.VisualStudio.CodeAnalysis.RuleSets;
using Microsoft.VisualStudio.Shell.Interop;
using SonarLint.VisualStudio.Integration.Resources;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SonarLint.VisualStudio.Integration
{
    internal class RuleSetInspector : IRuleSetInspector
    {
        private readonly IServiceProvider serviceProvider;
        private readonly HashSet<string> ruleSetSearchDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly RuleAction[] ruleActionStrictnessOrder = new RuleAction[]
        {
            RuleAction.None,
            RuleAction.Hidden,
            RuleAction.Info,
            RuleAction.Warning,
            RuleAction.Error
        };

        /// <summary>
        /// Relative path from VS install to the rule sets folder that come with VS
        /// </summary>
        public const string DefaultVSRuleSetsFolder = @"Team Tools\Static Analysis Tools\Rule Sets";

        public RuleSetInspector(IServiceProvider serviceProvider, params string[] userRuleSetDirectories)
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            this.serviceProvider = serviceProvider;
            ruleSetSearchDirectories.UnionWith(userRuleSetDirectories);
            ruleSetSearchDirectories.Add(GetStaticAnalysisToolsDirectory());
        }

        /// <summary>
        /// <see cref="IRuleSetInspector.FindConflictingRules(string, string)"/>
        /// </summary>
        public RuleSet FixConflictingRules(string baselineRuleSetPath, string targetRuleSetPath)
        {
            if (string.IsNullOrWhiteSpace(baselineRuleSetPath))
            {
                throw new ArgumentNullException(nameof(baselineRuleSetPath));
            }

            if (string.IsNullOrWhiteSpace(targetRuleSetPath))
            {
                throw new ArgumentNullException(nameof(targetRuleSetPath));
            }


            RuleSet baseline = RuleSet.LoadFromFile(baselineRuleSetPath);
            RuleSet target = RuleSet.LoadFromFile(targetRuleSetPath);

            RuleConflictInfo conflicts = this.FindConlictsCore(baseline, target);
            Debug.Assert(conflicts.HasConflicts, "There are no conflicts between baseline and target.");
            if (conflicts.HasConflicts)
            {
                if (!this.TryResolveIncludeConflicts(baseline, target))
                {
                    this.DeleteConflictingRules(baseline, target);
                }
            }

            return target;
        }

        /// <summary>
        /// <see cref="IRuleSetInspector.FindConflictingRules(string, string)"/>
        /// </summary>
        public RuleConflictInfo FindConflictingRules(string baseLineRuleSet, string targetRuleSet)
        {
            if (string.IsNullOrWhiteSpace(baseLineRuleSet))
            {
                throw new ArgumentNullException(nameof(baseLineRuleSet));
            }

            if (string.IsNullOrWhiteSpace(targetRuleSet))
            {
                throw new ArgumentNullException(nameof(targetRuleSet));
            }

            RuleSet baseline = RuleSet.LoadFromFile(baseLineRuleSet);
            RuleSet target = RuleSet.LoadFromFile(targetRuleSet);

            return FindConlictsCore(baseline, target);
        }

        private RuleConflictInfo FindConlictsCore(RuleSet baselineRuleSet, RuleSet targetRuleSet)
        {
            string[] ruleSetDirectories = this.ruleSetSearchDirectories.Union(new[]
            {
                    Path.GetDirectoryName(baselineRuleSet.FilePath),
                    Path.GetDirectoryName(targetRuleSet.FilePath)
                }).ToArray();

            // RuleProviders are used in practice for IncludeAll purposes i.e. AllRule.ruleset will be 
            // included with the specified action. In out cases only care about the baseline so we could create
            // a wrapper that provides the baseline rules, but in the context of the problem this is not required
            // since we will be able to find that the rules were missing just by diffing with the results from 
            // GetEffectiveRules, see more details below.
            RuleInfoProvider[] providers = new RuleInfoProvider[0];

            // Underlying implementation details of GetEffectiveRules:
            // The method will return a list of rules, some are the same as specified, and for some the Action will change 
            // according to the merge rules:

            // 1. If there's an <IncludeAll Action="..." /> then all the provider's rules will include all the rules 
            // with the specified Action (can't be None or Default).
            //
            // 2. Then the includes will be take into account (essentially the same flow 1-3, recursively):
            //    a. If the Include Action is None -> will not be included
            //    b. If the Include Action is Default -> rule actions remains as defined
            //    b. otherwise it will get the Action from the Include
            //  Once the Include rule Action is determined it will be merged but could only change to more strict value !
            // 
            // 3. Lastly the file-level rules are added with their Action (overriding any previous settings).
            //
            // To summarize in practical terms: 
            // (●) IncludeAll - will not impact us because all the baseline rules will be merged on top of (1)
            //  and will either be more strict or the IncludeAll action will be more strict 
            //  => either way won't weaken.
            // 
            // (●) By default i.e. the way we generate the targetRuleSet (which we expect it to be the project level ruleset),
            // the includes cannot weaken the base line ruleset (which we expect it to be the solution level ruleset)
            // due to to the include merge rules which prefer strictness and the fact that we use Default as the Include Action.
            // 
            // (●) The only thing that can weaken the rules in practice, in the default case, are ruleset level rules 
            // i.e what you get when you modify the ruleset by using the ruleset editor UI.
            // 
            // (●) In the none default case, when the user decides to manually edit the file, setting the baseline include
            // to None or removing it will use the other Includes' Action settings which could weaken the target ruleset.
            // 
            // With that in mind, we will find "conflicts" of two types - rule with weaker Action or a missing rule.
            // (assuming that all the rules were found with the help of ruleSetDirectories).
            // 
            // In terms of how to fix the conflict...  we could either just add all the conflicted rules into the project. That 
            // will not be optimal in terms of the user or the rebind/update experience. A better approach would be something like:
            // 1. Reset the baseline Include to Action=Default
            // 2. If there are still conflicts, remove all the conflicting rules which are directly on target ruleset.
            // At this point we should not have any conflicts, so there's should not be a need to add the remaining conflicting 
            // rules directly under the target with Action=TheExpectedAction

            var effectiveRulesMap = targetRuleSet.GetEffectiveRules(ruleSetDirectories, providers, this.EffectiveRulesErrorHandler)
                .ToDictionary(r => r.FullId, StringComparer.OrdinalIgnoreCase);

            var deprioritizedRules = baselineRuleSet.Rules.Select(r =>
            {
                RuleReference reference;
                if (effectiveRulesMap.TryGetValue(r.FullId, out reference))
                {
                    Debug.Assert(reference.Action != RuleAction.None, "Expected to be found in the missing set");

                    if (IsBaselineWeakend(r.Action, reference.Action))
                    {
                        return reference;
                    }
                }

                return null;
            }).Where(r => r != null);

            var disabledRules = baselineRuleSet.Rules.Where(r => !effectiveRulesMap.ContainsKey(r.FullId));

            return new RuleConflictInfo(disabledRules, deprioritizedRules);
        }

        /// <summary>
        /// Attempts to fix the conflicts by updating the RuleSet Includes to what they were 
        /// when we bound the solution to SonarQube
        /// </summary>
        /// <returns>Whether all conflicts were resolved</returns>
        private bool TryResolveIncludeConflicts(RuleSet baselineRuleSet, RuleSet targetRuleSet)
        {
            Debug.Assert(baselineRuleSet != null);
            Debug.Assert(targetRuleSet != null);
            Debug.Assert(!string.IsNullOrWhiteSpace(baselineRuleSet.FilePath));
            Debug.Assert(!string.IsNullOrWhiteSpace(targetRuleSet.FilePath));

            RuleSetHelper.UpdateExistingProjectRuleSet(targetRuleSet, targetRuleSet.FilePath, baselineRuleSet.FilePath);
            RuleConflictInfo conflicts1stAttempt = this.FindConlictsCore(baselineRuleSet, targetRuleSet);
            return !conflicts1stAttempt.HasConflicts;
        }

        /// <summary>
        /// Fixes conflicts resulting in having rule overrides in <param name="targetRuleSet" />
        /// </summary>
        /// <remarks>Assumes that <see cref="ResetSolutionRuleSetIncludes"/> executed already to fix the include issues</remarks>
        private void DeleteConflictingRules(RuleSet baselineRuleSet, RuleSet targetRuleSet)
        {
            // At this point the remaining conflicts are the rule overrides directly on target.
            // Removing those issues should fix all the remaining conflicts.
            foreach (RuleReference baselineRule in baselineRuleSet.Rules)
            {
                RuleReference targetRule;
                if (targetRuleSet.Rules.TryGetRule(baselineRule.FullId, out targetRule)
                    && IsBaselineWeakend(baselineRule.Action, targetRule.Action))
                {
                    targetRuleSet.Rules.Remove(targetRule);
                }
            }

            Debug.Assert(!this.FindConlictsCore(baselineRuleSet, targetRuleSet).HasConflicts, "Not expecting any conflicts once deleted the conflicting baseline rules on target");
        }

        internal /*for testing purposes*/ static bool IsBaselineWeakend(RuleAction baselineAction, RuleAction targetAction)
        {
            Debug.Assert(baselineAction != RuleAction.Default, "'Default' is invalid value for rule. RuleSet schema should prevent this");
            Debug.Assert(targetAction != RuleAction.Default, "'Default' is invalid value for rule. RuleSet schema should prevent this");

            int baselineStrictness = Array.IndexOf(ruleActionStrictnessOrder, baselineAction);
            int targetStrictness = Array.IndexOf(ruleActionStrictnessOrder, targetAction);

            return baselineStrictness > targetStrictness;
        }

        private void EffectiveRulesErrorHandler(string message, Exception error)
        {
            VsShellUtils.WriteToGeneralOutputPane(this.serviceProvider, Strings.UnexpectedErrorMessageFormat, typeof(RuleSetInspector).FullName, message, Constants.SonarLintIssuesWebUrl);
            Debug.Fail(message, error.ToString());
        }

        private string GetStaticAnalysisToolsDirectory()
        {
            // Get the VS install directory
            IVsShell shell = (IVsShell)this.serviceProvider.GetService(typeof(SVsShell));
            Debug.Assert(shell != null, "IVsShell is expected");

            object value;
            ErrorHandler.ThrowOnFailure(shell.GetProperty((int)__VSSPROPID2.VSSPROPID_InstallRootDir, out value));
            string vsInstallDirectory = value as string;

            return Path.Combine(vsInstallDirectory, DefaultVSRuleSetsFolder);
        }
    }
}
