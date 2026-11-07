using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Keeps private libraries out of newly-created users' default all-library policy.</summary>
public sealed class LibraryPrivacy(Access access, IUserManager users, ILibraryManager library)
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task Enforce()
    {
        if (Plugin.Instance.Configuration.Libraries.Count == 0) return;
        await _gate.WaitAsync();
        try
        {
            var bindings = Plugin.Instance.Configuration.Libraries.ToArray();
            var all = library.GetVirtualFolders().Select(f => Guid.Parse(f.ItemId)).ToArray();
            foreach (var user in access.Users())
            {
                var policy = users.GetUserDto(user).Policy; if (policy.IsAdministrator) continue;
                var forbidden = bindings.Where(b => b.UserId != Rules.Owner(user.Id)).Select(b => Guid.Parse(b.LibraryId)).ToHashSet();
                var previous = policy.EnableAllFolders ? all : policy.EnabledFolders;
                var allowed = previous.Where(id => !forbidden.Contains(id)).ToArray();
                if (!policy.EnableAllFolders && policy.EnabledFolders.SequenceEqual(allowed)) continue;
                policy.EnableAllFolders = false; policy.EnabledFolders = allowed;
                await users.UpdatePolicyAsync(user.Id, policy);
            }
        }
        finally { _gate.Release(); }
    }
}
public sealed class UserCreatedConsumer(LibraryPrivacy privacy) : IEventConsumer<UserCreatedEventArgs>
{
    public Task OnEvent(UserCreatedEventArgs eventArgs) => privacy.Enforce();
}
/// <summary>User policy mutations complete before the HTTP response is returned. Other MVC actions have no extra work.</summary>
public sealed class LibraryPrivacyFilter(LibraryPrivacy privacy, Access access) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor before && before.ControllerName == "LibraryStructure"
            && before.ActionName is "AddVirtualFolder" or "AddMediaPath" or "UpdateMediaPath" or "UpdateLibraryOptions")
        {
            var candidates = context.ActionArguments.Values.SelectMany(v => MediaPaths(v, 0));
            var requestedId = context.ActionArguments.Values.Select(v => v?.GetType().GetProperty("Id")?.GetValue(v)?.ToString()).FirstOrDefault(v => v is not null);
            Guid.TryParse(requestedId, out var libraryId);
            if (candidates.Any(path => access.OverlapsPrivateLibrary(path) && !(before.ActionName == "UpdateLibraryOptions" && access.UnchangedLibraryRoot(libraryId, path))))
            {
                context.Result = new BadRequestObjectResult(new { error = "This path overlaps a private JellyTube library. Choose a separate directory." }); return;
            }
        }
        var result = await next();
        if (result.Exception is null && context.ActionDescriptor is ControllerActionDescriptor descriptor
            && descriptor.ControllerName == "User" && descriptor.ActionName is "UpdateUserPolicy" or "CreateUserByName") await privacy.Enforce();
    }
    internal static IEnumerable<string> MediaPaths(object? value, int depth)
    {
        if (value is null || value is string || depth > 4) yield break;
        if (value is System.Collections.IEnumerable list)
        {
            foreach (var entry in list)
            {
                if (entry is string location) { yield return location; continue; }
                foreach (var path in MediaPaths(entry, depth + 1)) yield return path;
            }
            yield break;
        }
        foreach (var property in value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0))
        {
            if (property.Name == "Path" && property.GetValue(value) is string path) { yield return path; continue; }
            if (property.Name is "LibraryOptions" or "PathInfos" or "MediaPath" or "PathInfo")
                foreach (var child in MediaPaths(property.GetValue(value), depth + 1)) yield return child;
        }
    }
}
