using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace GriefWarden.Patches;

[HarmonyPatch(typeof(EntityBoat), nameof(EntityBoat.DidUnmount))]
public class EntityBoatDidUnmountPatch {
    [HarmonyPostfix]
    public static void Postfix(EntityBoat __instance, EntityAgent entityAgent) {
        if (entityAgent is EntityPlayer playerEntity) {
            IPlayer player = playerEntity.Player;
            Vec3i entityPosition = __instance.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);

            Main.Database.AddEntityLog(player.PlayerName, player.PlayerUID, "INTERACTED", __instance.GetName(), __instance.EntityId.ToString(), null, entityPosition.X, entityPosition.Y, entityPosition.Z);
        }
    }
}
