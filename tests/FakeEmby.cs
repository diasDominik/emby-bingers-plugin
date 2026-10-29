// Functional in-memory stand-ins for the Emby SDK surface the plugin uses (same signatures as
// the documented SDK; see ../check/EmbyStubs.cs). Enough behaviour to run the sync end to end.
#pragma warning disable CS1591, CS0067
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Common { public interface IApplicationHost { } }

namespace MediaBrowser.Common.Configuration
{
    public interface IApplicationPaths
    {
        string PluginConfigurationsPath { get; }
        string DataPath { get; }
    }
}

namespace MediaBrowser.Common.Plugins
{
    using MediaBrowser.Model.Drawing;

    public abstract class BasePlugin
    {
        public virtual string Description => null;
        public virtual Guid Id => Guid.Empty;
        public abstract string Name { get; }
    }

    public interface IHasThumbImage
    {
        ImageFormat ThumbImageFormat { get; }
        Stream GetThumbImage();
    }
}

namespace MediaBrowser.Model.Drawing { public enum ImageFormat { Bmp, Gif, Jpg, Png, Webp } }

namespace MediaBrowser.Model.Plugins
{
    public class PluginPageInfo
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public bool EnableInMainMenu { get; set; }
        public bool EnableInUserMenu { get; set; }
        public string MenuSection { get; set; }
        public string MenuIcon { get; set; }
        public bool IsMainConfigPage { get; set; }
    }
}

namespace MediaBrowser.Controller.Plugins
{
    using Emby.Web.GenericEdit;
    using MediaBrowser.Common;
    using MediaBrowser.Common.Plugins;

    public abstract class BasePluginSimpleUI<TOptionType> : BasePlugin where TOptionType : EditableOptionsBase, new()
    {
        public static TOptionType Stored = new TOptionType();

        protected BasePluginSimpleUI(IApplicationHost applicationHost) { }
        protected TOptionType GetOptions() => Stored;
        protected virtual TOptionType OnBeforeShowUI(TOptionType options) => options;
        protected virtual void OnOptionsSaved(TOptionType options) { }
        protected virtual bool OnOptionsSaving(TOptionType options) => true;
        protected virtual void OnCreatePageInfo(MediaBrowser.Model.Plugins.PluginPageInfo pageInfo) { }

        // Test hooks standing in for Emby's settings page.
        public TOptionType ShowUI() => this.OnBeforeShowUI(Stored);
        public MediaBrowser.Model.Plugins.PluginPageInfo PageInfo() { var p = new MediaBrowser.Model.Plugins.PluginPageInfo { Name = this.Name }; this.OnCreatePageInfo(p); return p; }
        public void Save(TOptionType options) { Stored = options; this.OnOptionsSaved(options); }
    }

    public interface IServerEntryPoint : IDisposable { void Run(); }
}

namespace Emby.Web.GenericEdit
{
    using Emby.Web.GenericEdit.Validation;

    public interface IEditableObject { }

    public abstract class EditableObjectBase : IEditableObject
    {
        public abstract string EditorTitle { get; }
        public virtual string EditorDescription => null;
        protected virtual void Validate(ValidationContext context) { }
        public List<string> RunValidation() { var c = new ValidationContext(); this.Validate(c); return c.Errors; }
    }

    public abstract class EditableOptionsBase : EditableObjectBase { }
}

namespace Emby.Web.GenericEdit.Validation
{
    public class ValidationContext
    {
        public List<string> Errors { get; } = new List<string>();
        public void AddValidationError(string message) => this.Errors.Add(message);
        public void AddValidationError(string propertyName, string message) => this.Errors.Add(propertyName + ": " + message);
    }
}

namespace Emby.Web.GenericEdit.Common
{
    public class EditorSelectOption
    {
        public EditorSelectOption() { }
        public EditorSelectOption(string value, string name, bool isEnabled = true, string color = null, string displayHint = null, string toolTip = null, string filterValue = null)
        { this.Value = value; this.Name = name; this.IsEnabled = isEnabled; }
        public string Name { get; set; }
        public string Value { get; set; }
        public bool IsEnabled { get; set; }
    }
}

namespace Emby.Web.GenericEdit.Elements
{
    public enum ItemStatus { Failed, InProgress, None, Succeeded, Unavailable, Unknown, Warning }

    public class StatusItem
    {
        public StatusItem() { }
        public StatusItem(string caption, string statusText) : this(caption, statusText, ItemStatus.None) { }
        public StatusItem(string caption, string statusText, ItemStatus status) { this.Caption = caption; this.StatusText = statusText; this.Status = status; }
        public string Caption { get; set; }
        public ItemStatus Status { get; set; }
        public string StatusText { get; set; }
        public override string ToString() => this.Status + ": " + this.StatusText;
    }
}

namespace MediaBrowser.Model.Attributes
{
    [AttributeUsage(AttributeTargets.Property)] public class IsPasswordAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property)]
    public class SelectItemsSourceAttribute : Attribute { public SelectItemsSourceAttribute(string sourcePropertyName) { } }
}

namespace MediaBrowser.Model.Logging
{
    public interface ILogger
    {
        void Debug(string message, params object[] paramList);
        void Error(string message, params object[] paramList);
        void ErrorException(string message, Exception exception, params object[] paramList);
        void Info(string message, params object[] paramList);
        void Warn(string message, params object[] paramList);
    }

    public interface ILogManager { ILogger GetLogger(string name); }

    public sealed class ConsoleLogger : ILogger, ILogManager
    {
        public List<string> Lines { get; } = new List<string>();
        public ILogger GetLogger(string name) => this;
        private void W(string level, string m, object[] p) { var s = level + " " + (p.Length > 0 ? string.Format(m, p) : m); lock (this.Lines) this.Lines.Add(s); Console.WriteLine("    [emby log] " + s); }
        public void Debug(string message, params object[] paramList) => this.W("DBG", message, paramList);
        public void Error(string message, params object[] paramList) => this.W("ERR", message, paramList);
        public void ErrorException(string message, Exception exception, params object[] paramList) => this.W("ERR", message + " :: " + exception, paramList);
        public void Info(string message, params object[] paramList) => this.W("INF", message, paramList);
        public void Warn(string message, params object[] paramList) => this.W("WRN", message, paramList);
    }
}

namespace MediaBrowser.Model.Tasks
{
    public class TaskTriggerInfo
    {
        public const string TriggerDaily = "DailyTrigger";
        public const string TriggerInterval = "IntervalTrigger";
        public const string TriggerStartup = "StartupTrigger";
        public long? IntervalTicks { get; set; }
        public string Type { get; set; }
    }

    public interface IScheduledTask
    {
        string Category { get; }
        string Description { get; }
        string Key { get; }
        string Name { get; }
        Task Execute(CancellationToken cancellationToken, IProgress<double> progress);
        IEnumerable<TaskTriggerInfo> GetDefaultTriggers();
    }
}

namespace MediaBrowser.Model.Entities
{
    public enum MetadataProviders { Imdb, Tmdb, Tvdb }

    public enum UserDataSaveReason { Import, PlaybackFinished, PlaybackProgress, PlaybackStart, ReportSearched, TogglePlayed, UpdateHideFromResume, UpdateUserRating }

    public interface IHasProviderIds { Dictionary<string, string> ProviderIds { get; } }

    public static class ProviderIdsExtensions
    {
        public static string GetProviderId(this IHasProviderIds instance, MetadataProviders provider) => instance.GetProviderId(provider.ToString());
        public static string GetProviderId(this IHasProviderIds instance, string name)
            => instance.ProviderIds.TryGetValue(name, out var v) ? v : null;
    }
}

namespace MediaBrowser.Controller.Entities
{
    using MediaBrowser.Model.Entities;

    public abstract class BaseItem : IHasProviderIds
    {
        private static long nextId = 1000;
        public Guid Id { get; set; } = Guid.NewGuid();
        public long InternalId { get; set; } = Interlocked.Increment(ref nextId);
        public int? IndexNumber { get; set; }
        public int? ParentIndexNumber { get; set; }
        public bool IsVirtualItem { get; set; }
        public virtual string Name { get; set; }
        public DateTimeOffset DateCreated { get; set; } = DateTimeOffset.UtcNow.AddDays(-400);
        public int? ProductionYear { get; set; }
        public long SeriesId { get; set; }
        public Dictionary<string, string> ProviderIds { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public override string ToString() => this.Name;
    }

    public sealed class User : BaseItem { }

    public class Video : BaseItem { }

    public class Folder : BaseItem { }

    public sealed class UserItemData
    {
        public DateTimeOffset? LastPlayedDate { get; set; }
        public long PlaybackPositionTicks { get; set; }
        public int PlayCount { get; set; }
        public bool Played { get; set; }
        public UserItemData Clone() => (UserItemData)this.MemberwiseClone();
    }

    public class InternalItemsQuery
    {
        public InternalItemsQuery() { }
        public InternalItemsQuery(User user) { this.User = user; }
        public ICollection<KeyValuePair<string, string>> AnyProviderIdEquals { get; set; }
        public string[] IncludeItemTypes { get; set; }
        public bool? IsPlayed { get; set; }
        public bool? IsVirtualItem { get; set; }
        public bool Recursive { get; set; }
        public long[] SeriesIds { get; set; }
        public User User { get; set; }
    }
}

namespace MediaBrowser.Controller.Entities.Movies { public class Movie : MediaBrowser.Controller.Entities.Video { } }

namespace MediaBrowser.Controller.Entities.TV
{
    public sealed class Series : MediaBrowser.Controller.Entities.Folder { }

    public sealed class Episode : MediaBrowser.Controller.Entities.Video
    {
        public int? IndexNumberEnd { get; set; }
        public Series SeriesRef { get; set; }
        public Series Series => this.SeriesRef;
        public string SeriesName { get; set; }
    }
}

namespace MediaBrowser.Controller.Library
{
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Model.Entities;

    public sealed class UserDataSaveEventArgs : EventArgs
    {
        public BaseItem Item { get; set; }
        public UserDataSaveReason SaveReason { get; set; }
        public User User { get; set; }
        public UserItemData UserData { get; set; }
    }

    public interface IUserDataManager
    {
        UserItemData GetUserData(User user, BaseItem item);
        void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken);
        event EventHandler<UserDataSaveEventArgs> UserDataSaved;
    }

    public interface IUserManager
    {
        [Obsolete("Use GetUsers.")]
        User[] Users { get; }
        User GetUserById(string id);
    }

    public interface ILibraryManager
    {
        BaseItem GetItemById(long id);
        BaseItem[] GetItemList(InternalItemsQuery query);
    }

    /// <summary>One in-memory Emby server: library, users and per-user played state.</summary>
    public sealed class FakeEmby : ILibraryManager, IUserManager, IUserDataManager
    {
        public List<BaseItem> Items { get; } = new List<BaseItem>();
        public List<User> UserList { get; } = new List<User>();
        public Dictionary<(long, long), UserItemData> Data { get; } = new Dictionary<(long, long), UserItemData>();
        public List<(string item, UserDataSaveReason reason, bool played)> Saves { get; } = new List<(string, UserDataSaveReason, bool)>();

        public event EventHandler<UserDataSaveEventArgs> UserDataSaved;

        User[] IUserManager.Users => this.UserList.ToArray();

        public User GetUserById(string id) => Guid.TryParse(id, out var g) ? this.UserList.FirstOrDefault(u => u.Id == g) : null;

        public BaseItem GetItemById(long id) => this.Items.FirstOrDefault(i => i.InternalId == id);

        public BaseItem[] GetItemList(InternalItemsQuery q)
        {
            IEnumerable<BaseItem> r = this.Items;
            if (q.IncludeItemTypes != null) r = r.Where(i => q.IncludeItemTypes.Contains(i.GetType().Name));
            if (q.IsVirtualItem.HasValue) r = r.Where(i => i.IsVirtualItem == q.IsVirtualItem.Value);
            if (q.SeriesIds != null) r = r.Where(i => q.SeriesIds.Contains(i.SeriesId));
            if (q.AnyProviderIdEquals != null) r = r.Where(i => q.AnyProviderIdEquals.Any(kv => string.Equals(i.GetProviderId(kv.Key), kv.Value, StringComparison.Ordinal)));
            if (q.IsPlayed.HasValue) r = r.Where(i => this.GetUserData(q.User, i).Played == q.IsPlayed.Value);
            return r.ToArray();
        }

        public UserItemData GetUserData(User user, BaseItem item)
        {
            if (!this.Data.TryGetValue((user.InternalId, item.InternalId), out var d))
            {
                this.Data[(user.InternalId, item.InternalId)] = d = new UserItemData();
            }

            return d.Clone(); // like Emby: callers get a copy and must save it
        }

        public void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken)
        {
            this.Data[(user.InternalId, item.InternalId)] = userData.Clone();
            lock (this.Saves) this.Saves.Add((item.Name, reason, userData.Played));
            this.UserDataSaved?.Invoke(this, new UserDataSaveEventArgs { Item = item, User = user, UserData = userData.Clone(), SaveReason = reason });
        }

        // What a person does in the Emby UI.
        public void UserToggles(User user, BaseItem item, bool played)
        {
            var d = this.GetUserData(user, item);
            d.Played = played;
            if (played) { d.PlayCount++; d.LastPlayedDate = DateTimeOffset.UtcNow; }
            this.SaveUserData(user, item, d, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        }

        public void UserFinishesPlayback(User user, BaseItem item)
        {
            var d = this.GetUserData(user, item);
            d.Played = true; d.PlayCount++; d.LastPlayedDate = DateTimeOffset.UtcNow;
            this.SaveUserData(user, item, d, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
        }
    }

    public sealed class FakePaths : MediaBrowser.Common.Configuration.IApplicationPaths
    {
        public FakePaths(string root) { this.PluginConfigurationsPath = Path.Combine(root, "plugins", "configurations"); this.DataPath = Path.Combine(root, "data"); }
        public string PluginConfigurationsPath { get; }
        public string DataPath { get; }
    }
}
