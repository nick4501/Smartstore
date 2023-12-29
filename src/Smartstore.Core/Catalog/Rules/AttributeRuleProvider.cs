﻿using Autofac;
using Smartstore.Core.Catalog.Attributes;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules;

namespace Smartstore.Core.Catalog.Rules
{
    public class AttributeRuleProvider : RuleProviderBase, IAttributeRuleProvider
    {
        private readonly SmartDbContext _db;
        private readonly IWorkContext _workContext;
        private readonly IComponentContext _componentContext;
        private readonly IRuleService _ruleService;

        public AttributeRuleProvider(
            SmartDbContext db,
            IWorkContext workContext,
            IComponentContext componentContext,
            IRuleService ruleService)
            : base(RuleScope.ProductAttribute)
        {
            _db = db;
            _workContext = workContext;
            _componentContext = componentContext;
            _ruleService = ruleService;
        }

        //public Localizer T { get; set; } = NullLocalizer.Instance;

        public IRule<AttributeRuleContext> GetProcessor(RuleExpression expression)
        {
            var group = expression as RuleExpressionGroup;
            var descriptor = expression.Descriptor as AttributeRuleDescriptor;

            if (group == null && descriptor == null)
            {
                throw new InvalidOperationException($"Missing attribute rule descriptor for expression {expression.Id} ('{expression.RawValue.EmptyNull()}').");
            }

            IRule<AttributeRuleContext> instance;

            if (group == null && descriptor.ProcessorType != typeof(AttributeCompositeRule))
            {
                instance = _componentContext.ResolveKeyed<IRule<AttributeRuleContext>>(descriptor.ProcessorType);
            }
            else
            {
                instance = new AttributeCompositeRule(group, this);
            }

            return instance;
        }

        public async Task<IRuleExpressionGroup> CreateExpressionGroupAsync(ProductVariantAttribute attribute, bool includeHidden = false)
        {
            Guard.NotNull(attribute);

            if (attribute?.RuleSetId == null)
            {
                return VisitRuleSet(null);
            }

            await _db.LoadReferenceAsync(attribute, x => x.RuleSet, false, q => q.Include(x => x.Rules));

            var group = await _ruleService.CreateExpressionGroupAsync(attribute.RuleSet, this, includeHidden);
            await _ruleService.ApplyMetadataAsync(group);

            return group;
        }

        public override async Task<IRuleExpression> VisitRuleAsync(RuleEntity rule)
        {
            var expression = new RuleExpression();
            await base.ConvertRuleAsync(rule, expression);
            return expression;
        }

        public override IRuleExpressionGroup VisitRuleSet(RuleSetEntity ruleSet)
        {
            return new RuleExpressionGroup
            {
                Id = ruleSet?.Id ?? 0,
                LogicalOperator = ruleSet?.LogicalOperator ?? LogicalRuleOperator.And,
                IsSubGroup = ruleSet?.IsSubGroup ?? false,
                Value = ruleSet?.Id ?? 0,
                RawValue = ruleSet?.Id.ToString() ?? "0",
                Provider = this,
                Descriptor = new AttributeRuleDescriptor
                {
                    RuleType = RuleType.Boolean,
                    ProcessorType = typeof(AttributeCompositeRule)
                }
            };
        }

        public async Task<bool> RuleMatchesAsync(AttributeRuleContext context, LogicalRuleOperator logicalOperator = LogicalRuleOperator.And)
        {
            Guard.NotNull(context);

            if (context.Attribute?.RuleSetId == null)
            {
                return true;
            }

            await _db.LoadReferenceAsync(context.Attribute, x => x.RuleSet, false, q => q.Include(x => x.Rules));

            var rules = await _ruleService.CreateExpressionGroupAsync(context.Attribute.RuleSet, this);

            var expressions = rules?.Expressions
                ?.Select(x => x as RuleExpression)
                ?.Where(x => x != null)
                ?.ToArray();

            if (expressions.IsNullOrEmpty())
            {
                return true;
            }

            RuleExpressionGroup group;

            if (expressions.Length == 1 && expressions[0] is RuleExpressionGroup group2)
            {
                group = group2;
            }
            else
            {
                group = new RuleExpressionGroup { LogicalOperator = logicalOperator };
                group.AddExpressions(expressions);
            }

            var processor = GetProcessor(group);
            var result = await processor.MatchAsync(context, group);

            return result;
        }

        protected override async Task<IEnumerable<RuleDescriptor>> LoadDescriptorsAsync()
        {
            var descriptors = new List<AttributeRuleDescriptor>();
            var language = _workContext.WorkingLanguage;
            var query = _db.ProductAttributes.AsNoTracking().OrderBy(x => x.DisplayOrder);
            var pageIndex = -1;

            while (true)
            {
                var variants = await query.ToPagedList(++pageIndex, 1000).LoadAsync();
                foreach (var variant in variants)
                {
                    var descriptor = new AttributeRuleDescriptor
                    {
                        Name = $"Variant{variant.Id}",
                        DisplayName = variant.GetLocalized(x => x.Name, language, true, false),
                        GroupKey = "Admin.Catalog.Attributes.ProductAttributes",
                        RuleType = RuleType.IntArray,
                        SelectList = new RemoteRuleValueSelectList(KnownRuleOptionDataSourceNames.VariantValue) { Multiple = true },
                        Operators = new[] { RuleOperator.In }
                    };
                    descriptor.Metadata["ParentId"] = variant.Id;
                    descriptor.Metadata["ValueType"] = ProductVariantAttributeValueType.Simple;

                    descriptors.Add(descriptor);
                }

                if (!variants.HasNextPage)
                {
                    break;
                }
            }

            return descriptors;
        }
    }
}
