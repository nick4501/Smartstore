﻿using System.Threading.Tasks;
using Smartstore.Core.Localization;
using Smartstore.Core.Security;
using Smartstore.Events;
using Smartstore.Forum.Models;
using Smartstore.Web.Modelling;
using Smartstore.Web.Rendering.Events;

namespace Smartstore.Forum
{
    public class Events : IConsumer
    {
        public Localizer T { get; set; }

        public async Task HandleEventAsync(TabStripCreated message, IPermissionService permissions)
        {
            // Render tab with forum search settings.
            if (message.TabStripName.EqualsNoCase("searchsettings-edit"))
            {
                if (await permissions.AuthorizeAsync(ForumPermissions.Read))
                {
                    await message.TabFactory.AddAsync(builder => builder
                        .Text(T("Forum.Forum"))
                        .Name("tab-search-forum")
                        .LinkHtmlAttributes(new { data_tab_name = "ForumSearchSettings" })
                        .Action("ForumSearchSettings", "Forum", new { area = "Admin" })
                        .Ajax());
                }
            }
        }

        public async Task HandleEventAsync(ModelBoundEvent message, IPermissionService permissions)
        {
            var model = message.BoundModel.CustomProperties.ContainsKey("ForumSearchSettings")
                ? message.BoundModel.CustomProperties["ForumSearchSettings"] as ForumSearchSettingsModel
                : null;

            if (model == null || !await permissions.AuthorizeAsync(ForumPermissions.Read))
            {
                return;
            }

            $"-- Bound ForumSearchSettingsModel".Dump();


        }
    }
}
