using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;

namespace Emby.Bingers
{
    /// <summary>Settings shown on the plugin's page in the Emby dashboard.</summary>
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Bingers";

        public override string EditorDescription =>
            "Syncs watched movies and episodes between one Emby user and your Bingers account.\n"
            + "Create a 'read + write' personal token at https://bingers.app/integrations (needs the Legend or Icon plan).";

        [XmlIgnore]
        [DisplayName("Connection")]
        public StatusItem Connection { get; set; } = new StatusItem("Bingers", "Not configured", ItemStatus.Unavailable);

        [XmlIgnore]
        [DisplayName("Last sync")]
        public StatusItem LastSync { get; set; } = new StatusItem("Sync", "Never run", ItemStatus.None);

        [DisplayName("Personal access token")]
        [Description("Starts with bng_pat_. Choose 'read + write' when creating it.")]
        [IsPassword]
        public string AccessToken { get; set; }

        [Browsable(false)]
        [XmlIgnore]
        public List<EditorSelectOption> UserChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Emby user")]
        [Description("Whose watched status to sync.")]
        [SelectItemsSource(nameof(UserChoices))]
        public string EmbyUserId { get; set; }

        [DisplayName("Send Emby watches to Bingers")]
        [Description("Finished playback and items marked played in Emby are recorded on Bingers.")]
        public bool SyncToBingers { get; set; } = true;

        [DisplayName("Bring Bingers watches into Emby")]
        [Description("Movies and episodes watched on Bingers (or anywhere it hears about) are marked played in Emby. Checked every 15 minutes by the 'Bingers: sync from Bingers' scheduled task.")]
        public bool SyncFromBingers { get; set; } = true;

        [DisplayName("Send 'mark unplayed' to Bingers")]
        [Description("Off by default: unmarking in Emby deletes that watch from your Bingers history.")]
        public bool SendUnwatchesToBingers { get; set; }

        [DisplayName("Apply Bingers unwatches in Emby")]
        [Description("Off by default: when a watch is removed on Bingers, mark the item unplayed in Emby.")]
        public bool ApplyUnwatchesFromBingers { get; set; }

        protected override void Validate(ValidationContext context)
        {
            var token = this.AccessToken?.Trim();
            if (!string.IsNullOrEmpty(token) && !token.StartsWith("bng_pat_", System.StringComparison.Ordinal))
            {
                context.AddValidationError(nameof(this.AccessToken), "A Bingers personal token starts with bng_pat_.");
            }

            if (!string.IsNullOrEmpty(token) && string.IsNullOrEmpty(this.EmbyUserId))
            {
                context.AddValidationError(nameof(this.EmbyUserId), "Choose the Emby user to sync.");
            }
        }
    }
}
