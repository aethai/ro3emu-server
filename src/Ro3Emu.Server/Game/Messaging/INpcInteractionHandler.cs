using Romsg;

namespace Ro3Emu.Server.Game.Messaging;

/// <summary>
/// Single ordered registry for <see cref="SceneService.NpcInteraction"/> (32700) handlers.
/// Handlers run in ascending <see cref="Order"/>; the first that returns true owns the interaction.
/// </summary>
public interface INpcInteractionHandler
{
    int Order { get; }
    Task<bool> TryInteractAsync(GameSession session, NpcInteractionReq request);
}
