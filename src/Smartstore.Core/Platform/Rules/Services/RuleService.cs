﻿using Autofac;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules.Rendering;

namespace Smartstore.Core.Rules
{
    public partial class RuleService : IRuleService
    {
        private readonly SmartDbContext _db;
        private readonly IWorkContext _workContext;
        private readonly Lazy<IEnumerable<IRuleOptionsProvider>> _ruleOptionsProviders;

        public RuleService(
            SmartDbContext db,
            IWorkContext workContext,
            Lazy<IEnumerable<IRuleOptionsProvider>> ruleOptionsProviders)
        {
            _db = db;
            _workContext = workContext;
            _ruleOptionsProviders = ruleOptionsProviders;
        }

        public Localizer T { get; set; } = NullLocalizer.Instance;

        public virtual async Task<bool> ApplyRuleSetMappingsAsync<T>(T entity, int[] selectedRuleSetIds)
            where T : BaseEntity, IRulesContainer
        {
            Guard.NotNull(entity);

            selectedRuleSetIds ??= Array.Empty<int>();

            await _db.LoadCollectionAsync(entity, x => x.RuleSets);

            if (!selectedRuleSetIds.Any() && !entity.RuleSets.Any())
            {
                // Nothing to do.
                return false;
            }

            var updated = false;
            var allRuleSets = await _db.RuleSets
                .AsQueryable() // Prevent ambiguous extension method call.
                .Where(x => !x.IsSubGroup)
                .ToDictionaryAsync(x => x.Id);

            foreach (var ruleSetId in allRuleSets.Keys)
            {
                if (selectedRuleSetIds.Contains(ruleSetId))
                {
                    if (!entity.RuleSets.Any(x => x.Id == ruleSetId))
                    {
                        entity.RuleSets.Add(allRuleSets[ruleSetId]);
                        updated = true;
                    }
                }
                else if (entity.RuleSets.Any(x => x.Id == ruleSetId))
                {
                    entity.RuleSets.Remove(allRuleSets[ruleSetId]);
                    updated = true;
                }
            }

            return updated;
        }

        public async Task<IRuleExpressionGroup> CreateExpressionGroupAsync(int ruleSetId, IRuleVisitor visitor, bool includeHidden = false)
        {
            if (ruleSetId <= 0)
            {
                return null;
            } 

            // TODO: prevent stack overflow > check if nested groups reference each other.

            var ruleSet = await _db.RuleSets
                .AsNoTracking()
                .Include(x => x.Rules)
                .FirstOrDefaultAsync(x => x.Id == ruleSetId);

            if (ruleSet == null)
            {
                // TODO: ErrHandling (???)
                return null;
            }

            return await CreateExpressionGroupAsync(ruleSet, visitor, includeHidden);
        }

        public virtual async Task<IRuleExpressionGroup> CreateExpressionGroupAsync(RuleSetEntity ruleSet, IRuleVisitor visitor, bool includeHidden = false)
        {
            if (ruleSet.Scope != visitor.Scope)
            {
                throw new InvalidOperationException($"Differing rule scope {ruleSet.Scope}. Expected {visitor.Scope}.");
            }

            if (!includeHidden && !ruleSet.IsActive)
            {
                return null;
            }

            await _db.LoadCollectionAsync(ruleSet, x => x.Rules);

            var group = visitor.VisitRuleSet(ruleSet);

            var expressions = await ruleSet.Rules
                .SelectAwait(x => CreateExpression(x, visitor))
                .Where(x => x != null)
                .AsyncToArray();

            group.AddExpressions(expressions);

            return group;
        }

        public virtual async Task ApplyMetadataAsync(IRuleExpressionGroup group, Language language = null)
        {
            if (group == null)
            {
                return;
            }

            language ??= _workContext.WorkingLanguage;

            foreach (var expression in group.Expressions)
            {
                if (expression is IRuleExpressionGroup subGroup)
                {
                    await ApplyMetadataAsync(subGroup, language);
                    continue;
                }

                if (!expression.Descriptor.IsValid)
                {
                    expression.Metadata["Error"] = T("Admin.Rules.InvalidDescriptor").Value;
                }

                // Load name and subtitle (e.g. SKU) for selected options.
                if (expression.Descriptor.SelectList is RemoteRuleValueSelectList list)
                {
                    var optionsProvider = _ruleOptionsProviders.Value.FirstOrDefault(x => x.Matches(list.DataSource));
                    if (optionsProvider != null)
                    {
                        var options = await optionsProvider.GetOptionsAsync(new RuleOptionsContext(RuleOptionsRequestReason.SelectedDisplayNames, expression)
                        {
                            PageSize = int.MaxValue,
                            Language = language
                        });

                        expression.Metadata["SelectedItems"] = options.Options.ToDictionarySafe(
                            x => x.Value,
                            x => new RuleSelectItem { Text = x.Text, Hint = x.Hint });
                    }
                }
            }
        }

        //private async Task<IRuleExpressionGroup> CreateExpressionGroup(RuleSetEntity ruleSet, RuleEntity refRule, IRuleVisitor visitor)
        //{
        //    if (ruleSet.Scope != visitor.Scope)
        //    {
        //        // TODO: ErrHandling (ruleSet is for a different scope)
        //        return null;
        //    }

        //    await _db.LoadCollectionAsync(ruleSet, x => x.Rules);

        //    var group = visitor.VisitRuleSet(ruleSet);
        //    if (refRule != null)
        //    {
        //        group.RefRuleId = refRule.Id;
        //    }

        //    var expressions = await ruleSet.Rules
        //        .SelectAwait(x => CreateExpression(x, visitor))
        //        .Where(x => x != null)
        //        .AsyncToArray();

        //    group.AddExpressions(expressions);

        //    return group;
        //}

        private async Task<IRuleExpression> CreateExpression(RuleEntity ruleEntity, IRuleVisitor visitor)
        {
            if (!ruleEntity.IsGroup)
            {
                return await visitor.VisitRuleAsync(ruleEntity);
            }

            // It's a group, do recursive call.
            var group = await CreateExpressionGroupAsync(ruleEntity.Value.Convert<int>(), visitor);
            if (group != null)
            {
                group.RefRuleId = ruleEntity.Id;
            }

            return group;
        }
    }
}
