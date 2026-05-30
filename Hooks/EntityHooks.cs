using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace GriefWarden.Hooks;

public class EntityHooks {
    public EntityHooks() {
        Main.API.Event.OnPlayerInteractEntity += this.OnPlayerInteractEntity;
        Main.API.Event.OnEntityDeath += this.OnEntityDeath;
        //Main.API.Event.OnEntitySpawn += this.OnEntitySpawn;
        //Main.API.Event.OnEntityDespawn += this.OnEntityDespawn;
    }

    private void OnPlayerInteractEntity(Entity entity, IPlayer player, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling) {
        if (entity == null || player == null)
            return;

        string? itemstack = Util.GetPlayerCurrentItemstackName(player);

        Vec3i entityPosition = entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);
        Main.Database.AddEntityLog(player.PlayerName, player.PlayerUID, "INTERACTED", entity.GetName(), entity.EntityId.ToString(), itemstack, entityPosition.X, entityPosition.Y, entityPosition.Z);
    }

    private void OnEntityDeath(Entity entity, DamageSource damageSource) {
        string? playername = null;
        string? playeruid = null;
        string? itemstack = null;
        string reasonAddon = "";

        if (damageSource != null) {
            Entity? causeEntity = damageSource.GetCauseEntity();
            if (causeEntity != null && causeEntity is EntityPlayer player) {
                playername = player.Player.PlayerName;
                playeruid = player.PlayerUID;

                itemstack = Util.GetPlayerCurrentItemstackName(player.Player);
            }
            else {
                //playername = damageSource.Source.ToString();
                //playeruid = "x";
                reasonAddon = " BECAUSE OF " + damageSource.Source.ToString();
            }
        }

        Vec3i entityPosition = entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);
        Main.Database.AddEntityLog(playername, playeruid, "KILLED", entity.GetName() + reasonAddon, entity.EntityId.ToString(), itemstack, entityPosition.X, entityPosition.Y, entityPosition.Z);
    }

    /*private void OnEntitySpawn(Entity entity) {
        Vec3i entityPosition = entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);
        Main.API.Logger.Debug(entity.GetType().Name + " spawned at " + entityPosition);
        Main.Database.AddEntityLog("NULL", "NULL", "SPAWNED", entity.GetType().Name, entity.EntityId.ToString(), "NULL", entityPosition.X, entityPosition.Y, entityPosition.Z);
    }

    private void OnEntityDespawn(Entity entity, EntityDespawnData despawnData) {
        Vec3i entityPosition = entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);
        Main.API.Logger.Debug(entity.GetType().Name + " despawned at " + entityPosition);
        Main.Database.AddEntityLog("NULL", "NULL", "DESPAWNED", entity.GetType().Name, entity.EntityId.ToString(), "NULL", entityPosition.X, entityPosition.Y, entityPosition.Z);
    }*/
}
