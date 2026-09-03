using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace vizo_backend.Services;

/// <summary>
/// The live wire behind the bell in the top bar.
///
/// ─────────────────────────── WHY THIS EXISTS ───────────────────────────────
///
/// Until now a notification reached the bell in one of two ways: a Web Push, or
/// the next time the page happened to reload. Web Push is the wrong tool for
/// the bell -- it needs the browser's permission prompt, it is refused outright
/// on iOS unless the app has been added to the home screen, and a person who
/// clicked "Block" three months ago silently never sees anything again.
///
/// So the bell gets its own channel. Somebody who is looking at the screen is
/// told immediately, over a connection the browser already trusts, with no
/// prompt and nothing to grant. Push stays for the phone that is in a pocket;
/// this is for the screen that is open.
///
/// ─────────────────────────── HOW IT IS ADDRESSED ───────────────────────────
///
/// Per user, by group. SignalR's own "user identifier" is taken from the
/// NameIdentifier claim -- which this API sets as the JWT's name claim -- so
/// Clients.User("7") already works. Groups are used anyway, and joined
/// explicitly on connect, because the group name is then the user id in plain
/// sight at both ends rather than a convention two files apart have to agree on.
///
/// A ROLE group is joined as well, so that something addressed to "every
/// warehouse keeper" is one send rather than a lookup and a loop.
///
/// ─────────────────────────── THE TOKEN ─────────────────────────────────────
///
/// A WebSocket handshake cannot carry an Authorization header. The browser
/// sends the JWT as ?access_token= instead, and Program.cs lifts it out in
/// OnMessageReceived for paths under /hubs. That is the standard arrangement
/// and the reason the hub can simply be [Authorize] like everything else.
/// </summary>
[Authorize]
public class NotificationHub : Hub
{
    public const string Path = "/hubs/notifications";

    /// <summary>The group carrying everything addressed to one person.</summary>
    public static string UserGroup(int userId) => $"user-{userId}";

    /// <summary>The group carrying everything addressed to a whole role.</summary>
    public static string RoleGroup(string roleKey) => $"role-{roleKey}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrWhiteSpace(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");

        var role = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        if (!string.IsNullOrWhiteSpace(role))
            await Groups.AddToGroupAsync(Context.ConnectionId, RoleGroup(role));

        await base.OnConnectedAsync();
    }

    /* Leaving a group on disconnect is deliberately not done: SignalR removes
       the connection from every group it holds when it drops, and doing it by
       hand only adds a race with a reconnect that has already been made. */
}
