using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace GriefWarden;

public class Database {
    private SqliteConnection connection;
    private int logLimit = 4;

    private string createBlockLogsTable = @"CREATE TABLE IF NOT EXISTS blocklogs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp TEXT,
        playername TEXT,
        playeruid TEXT,
        actiontype TEXT,
        block TEXT,
        itemstack TEXT,
        x INTEGER,
        y INTEGER,
        z INTEGER
    )";
    private string createEntityLogsTable = @"CREATE TABLE IF NOT EXISTS entitylogs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp TEXT,
        playername TEXT,
        playeruid TEXT,
        actiontype TEXT,
        entityname TEXT,
        entityid TEXT,
        itemstack TEXT,
        x INTEGER,
        y INTEGER,
        z INTEGER
    )";
    private string createContainerLogsTable = @"CREATE TABLE IF NOT EXISTS containerlogs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp TEXT,
        playername TEXT,
        playeruid TEXT,
        containerid TEXT,
        itemstack TEXT,
        quantity INTEGER
    )";

    public Database() {
        string dbPath = Path.GetFullPath(Path.Combine(Main.API.GetOrCreateDataPath("GriefWarden"), "database.db"));

        connection = new SqliteConnection("Data Source=" + dbPath);
        connection.Open();

        var blockLogsCmd = connection.CreateCommand();
        blockLogsCmd.CommandText = createBlockLogsTable;
        blockLogsCmd.ExecuteNonQuery();

        var entityLogsCmd = connection.CreateCommand();
        entityLogsCmd.CommandText = createEntityLogsTable;
        entityLogsCmd.ExecuteNonQuery();

        var containerLogsCmd = connection.CreateCommand();
        containerLogsCmd.CommandText = createContainerLogsTable;
        containerLogsCmd.ExecuteNonQuery();
    }

    public void AddBlockLog(string? playername, string? playeruid, string actiontype, string block, string? itemstack, int x, int y, int z) {
        // May need to use transactions in live test

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO blocklogs (timestamp, playername, playeruid, actiontype, block, itemstack, x, y, z)
        VALUES ($timestamp, $playername, $playeruid, $actiontype, $block, $itemstack, $x, $y, $z)";

        cmd.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$playername", playername != null ? playername : DBNull.Value);
        cmd.Parameters.AddWithValue("$playeruid", playeruid != null ? playeruid : DBNull.Value);
        cmd.Parameters.AddWithValue("$actiontype", actiontype);
        cmd.Parameters.AddWithValue("$block", block);
        cmd.Parameters.AddWithValue("$itemstack", itemstack != null ? itemstack : DBNull.Value);
        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$z", z);

        cmd.ExecuteNonQuery();
    }

    public void CheckBlockLog(int pageNum, IServerPlayer player, int groupId, int x, int y, int z, int radius) {
        int skipLogsNum = logLimit * (pageNum - 1);

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT * FROM (
        SELECT * FROM blocklogs
        WHERE x BETWEEN $x - $radius AND $x + $radius
        AND y BETWEEN $y - $radius AND $y + $radius
        AND z BETWEEN $z - $radius AND $z + $radius
        ORDER BY id DESC
        LIMIT $loglimit
        OFFSET $skiplognum)
        ORDER BY id ASC";

        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$z", z);
        cmd.Parameters.AddWithValue("$radius", radius);
        cmd.Parameters.AddWithValue("$loglimit", logLimit);
        cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);

        using var reader = cmd.ExecuteReader();
        if (reader.HasRows) {
            Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, "<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>", EnumChatType.CommandSuccess);
            while (reader.Read()) {
                string timestamp = reader.GetString(1);
                string? playername = reader.IsDBNull(2) ? null : reader.GetString(2);
                string? playeruid = reader.IsDBNull(3) ? null : reader.GetString(3);
                string actiontype = reader.GetString(4);
                string block = reader.GetString(5);
                string? itemstack = reader.IsDBNull(6) ? null : reader.GetString(6);
                int logX = reader.GetInt32(7);
                int logY = reader.GetInt32(8);
                int logZ = reader.GetInt32(9);

                string playerStr = playername == null ? "" : "<strong>{1}</strong>({2}) ";
                string itemstackStr = itemstack == null ? "" : "with {5} ";
                string logString = String.Format("<strong><font color=\"#6F88DB\">{0}</font></strong> | " + playerStr + "{3} {4} " + itemstackStr + "@ <strong><font color=\"#9BD1EC\">{6}, {7}, {8}</font></strong>", timestamp, playername, playeruid, actiontype, block, itemstack, logX, logY, logZ);
                Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, logString, EnumChatType.CommandSuccess);
            }
        }
        else {
            Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, "No block logs found here.", EnumChatType.CommandSuccess);
        }
    }

    public void AddEntityLog(string? playername, string? playeruid, string actiontype, string entityname, string entityid, string? itemstack, int x, int y, int z) {
        // May need to use transactions in live test

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO entitylogs (timestamp, playername, playeruid, actiontype, entityname, entityid, itemstack, x, y, z)
        VALUES ($timestamp, $playername, $playeruid, $actiontype, $entityname, $entityid, $itemstack, $x, $y, $z)";

        cmd.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$playername", playername != null ? playername : DBNull.Value);
        cmd.Parameters.AddWithValue("$playeruid", playeruid != null ? playeruid : DBNull.Value);
        cmd.Parameters.AddWithValue("$actiontype", actiontype);
        cmd.Parameters.AddWithValue("$entityname", entityname);
        cmd.Parameters.AddWithValue("$entityid", entityid);
        cmd.Parameters.AddWithValue("$itemstack", itemstack != null ? itemstack : DBNull.Value);
        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$z", z);

        cmd.ExecuteNonQuery();
    }

    public void CheckEntityLog(int pageNum, IServerPlayer player, int groupId, int x, int y, int z, int radius) {
        int skipLogsNum = logLimit * (pageNum - 1);

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT * FROM (
        SELECT * FROM entitylogs
        WHERE x BETWEEN $x - $radius AND $x + $radius
        AND y BETWEEN $y - $radius AND $y + $radius
        AND z BETWEEN $z - $radius AND $z + $radius
        ORDER BY id DESC
        LIMIT $loglimit
        OFFSET $skiplognum)
        ORDER BY id ASC";

        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$z", z);
        cmd.Parameters.AddWithValue("$radius", radius);
        cmd.Parameters.AddWithValue("$loglimit", logLimit);
        cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);

        using var reader = cmd.ExecuteReader();
        if (reader.HasRows) {
            Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, "<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>", EnumChatType.CommandSuccess);
            while (reader.Read()) {
                string timestamp = reader.GetString(1);
                string? playername = reader.IsDBNull(2) ? null : reader.GetString(2);
                string? playeruid = reader.IsDBNull(3) ? null : reader.GetString(3);
                string actiontype = reader.GetString(4);
                string entityname = reader.GetString(5);
                string entityid = reader.GetString(6);
                string? itemstack = reader.IsDBNull(7) ? null : reader.GetString(7);
                int logX = reader.GetInt32(8);
                int logY = reader.GetInt32(9);
                int logZ = reader.GetInt32(10);

                string playerStr = playername == null ? "" : "<strong>{1}</strong>({2}) ";
                string itemstackStr = itemstack == null ? "" : "with {6} ";
                string logString = String.Format("<strong><font color=\"#6F88DB\">{0}</font></strong> | " + playerStr + "{3} {4}({5}) " + itemstackStr + "@ <strong><font color=\"#9BD1EC\">{7}, {8}, {9}</font></strong>", timestamp, playername, playeruid, actiontype, entityname, entityid, itemstack, logX, logY, logZ);
                Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, logString, EnumChatType.CommandSuccess);
            }
        }
        else {
            Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, "No entity logs found.", EnumChatType.CommandSuccess);
        }
    }

    public void AddContainerLog(string playername, string playeruid, string containerid, string itemstack, int quantity) {
        // May need to use transactions in live test

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO containerlogs (timestamp, playername, playeruid, containerid, itemstack, quantity)
        VALUES ($timestamp, $playername, $playeruid, $containerid, $itemstack, $quantity)";

        cmd.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$playername", playername);
        cmd.Parameters.AddWithValue("$playeruid", playeruid);
        cmd.Parameters.AddWithValue("$containerid", containerid);
        cmd.Parameters.AddWithValue("$itemstack", itemstack);
        cmd.Parameters.AddWithValue("$quantity", quantity);

        cmd.ExecuteNonQuery();
    }

    public void CheckContainerLog(int pageNum, IServerPlayer player, int groupId, string containerid) {
        CheckContainerLog(pageNum, player, groupId, new List<string> { containerid });
    }
    public void CheckContainerLog(int pageNum, IServerPlayer player, int groupId, List<string> containerids) {
        int skipLogsNum = logLimit * (pageNum - 1);

        // concat should be fine here because no user input, but I still don't like it too much
        string containerIDsQueryStr = "";
        for (int i = 0; i < containerids.Count; i++) {
            containerIDsQueryStr += "'" + containerids[i] + "'";
            if (i < containerids.Count - 1)
                containerIDsQueryStr += " OR containerid = ";
        }

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM (SELECT * FROM containerlogs WHERE containerid = " + containerIDsQueryStr + 
            " ORDER BY id DESC LIMIT $loglimit OFFSET $skiplognum) ORDER BY id ASC";

        //cmd.Parameters.AddWithValue("$containerid", containerid);
        cmd.Parameters.AddWithValue("$loglimit", logLimit);
        cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);

        using var reader = cmd.ExecuteReader();
        if (reader.HasRows) {
            Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, "<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>", EnumChatType.CommandSuccess);
            while (reader.Read()) {
                string timestamp = reader.GetString(1);
                string playername = reader.GetString(2);
                string playeruid = reader.GetString(3);
                string logContainerid = reader.GetString(4);
                string itemstack = reader.GetString(5);
                int quantity = reader.GetInt32(6);

                string logString = String.Format("<strong><font color=\"#6F88DB\">{0}</font></strong> | <strong>{1}</strong>({2}) TOOK {5}x{4} from <strong><font color=\"#9BD1EC\">{3}</font></strong>", timestamp, playername, playeruid, logContainerid, itemstack, quantity);
                Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, logString, EnumChatType.CommandSuccess);
            }
        }
        else {
            Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, "No container logs found.", EnumChatType.CommandSuccess);
        }
    }

    public void Dispose() {
        connection.Close();
    }
}
