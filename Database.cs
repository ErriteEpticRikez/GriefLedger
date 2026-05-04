using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace GriefWarden;

public class Database : IDisposable {
    private string dbPath;
    private int logLimit = 4;

    private Thread workerThread;
    private CancellationTokenSource cancellationTokenSource;
    private ConcurrentQueue<Action<SqliteConnection>> databaseTasks;

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
        quantity INTEGER,
        actiontype TEXT
    )";

    public Database() {
        dbPath = Path.GetFullPath(Path.Combine(Main.API.GetOrCreateDataPath("GriefWarden"), "database.db"));

        // Initialize schema and indices synchronously
        using (var connection = new SqliteConnection("Data Source=" + dbPath)) {
            connection.Open();

            // Enable WAL mode for performance
            using (var pragmaCmd = connection.CreateCommand()) {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
                pragmaCmd.ExecuteNonQuery();
                pragmaCmd.CommandText = "PRAGMA synchronous=NORMAL;";
                pragmaCmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = createBlockLogsTable;
                cmd.ExecuteNonQuery();
                cmd.CommandText = createEntityLogsTable;
                cmd.ExecuteNonQuery();
                cmd.CommandText = createContainerLogsTable;
                cmd.ExecuteNonQuery();

                // Add actiontype column to containerlogs if it doesn't exist (for backwards compatibility)
                cmd.CommandText = "PRAGMA table_info(containerlogs);";
                bool hasActionType = false;
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        if (reader.GetString(1) == "actiontype") {
                            hasActionType = true;
                            break;
                        }
                    }
                }
                if (!hasActionType) {
                    cmd.CommandText = "ALTER TABLE containerlogs ADD COLUMN actiontype TEXT DEFAULT 'TAKEN';";
                    cmd.ExecuteNonQuery();
                }

                // Indices
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_blocklogs_coords ON blocklogs(x, y, z);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_entitylogs_coords ON entitylogs(x, y, z);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_containerlogs_id ON containerlogs(containerid);";
                cmd.ExecuteNonQuery();
            }
        }

        databaseTasks = new ConcurrentQueue<Action<SqliteConnection>>();
        cancellationTokenSource = new CancellationTokenSource();
        workerThread = new Thread(WorkerLoop);
        workerThread.IsBackground = true;
        workerThread.Start();
    }

    private void WorkerLoop() {
        using var connection = new SqliteConnection("Data Source=" + dbPath);
        connection.Open();

        while (!cancellationTokenSource.IsCancellationRequested) {
            if (databaseTasks.TryDequeue(out var task)) {
                try {
                    task(connection);
                }
                catch (Exception ex) {
                    Main.API.Logger.Error("GriefWarden: Database worker encountered an error: " + ex);
                }
            }
            else {
                Thread.Sleep(10); // Sleep briefly if queue is empty
            }
        }

        // Process remaining tasks before exiting
        while (databaseTasks.TryDequeue(out var task)) {
            try {
                task(connection);
            }
            catch (Exception ex) {
                Main.API.Logger.Error("GriefWarden: Database worker encountered an error during shutdown: " + ex);
            }
        }
    }

    public void AddBlockLog(string? playername, string? playeruid, string actiontype, string block, string? itemstack, int x, int y, int z) {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        databaseTasks.Enqueue((connection) => {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO blocklogs (timestamp, playername, playeruid, actiontype, block, itemstack, x, y, z)
            VALUES ($timestamp, $playername, $playeruid, $actiontype, $block, $itemstack, $x, $y, $z)";

            cmd.Parameters.AddWithValue("$timestamp", timestamp);
            cmd.Parameters.AddWithValue("$playername", playername != null ? playername : DBNull.Value);
            cmd.Parameters.AddWithValue("$playeruid", playeruid != null ? playeruid : DBNull.Value);
            cmd.Parameters.AddWithValue("$actiontype", actiontype);
            cmd.Parameters.AddWithValue("$block", block);
            cmd.Parameters.AddWithValue("$itemstack", itemstack != null ? itemstack : DBNull.Value);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$z", z);

            cmd.ExecuteNonQuery();
        });
    }

    public void CheckBlockLog(int pageNum, IServerPlayer player, int groupId, int x, int y, int z, int radius) {
        // Read on a separate thread/connection to not block main thread
        System.Threading.Tasks.Task.Run(() => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();
            int skipLogsNum = logLimit * (pageNum - 1);

            using var cmd = connection.CreateCommand();
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

            var logs = new List<string>();
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    logs.Add("<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>");
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
                        logs.Add(logString);
                    }
                }
                else {
                    logs.Add("No block logs found here.");
                }
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                foreach (var log in logs) {
                    Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, log, EnumChatType.CommandSuccess);
                }
            }, "SendBlockLog");
        });
    }

    public void AddEntityLog(string? playername, string? playeruid, string actiontype, string entityname, string entityid, string? itemstack, int x, int y, int z) {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        databaseTasks.Enqueue((connection) => {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO entitylogs (timestamp, playername, playeruid, actiontype, entityname, entityid, itemstack, x, y, z)
            VALUES ($timestamp, $playername, $playeruid, $actiontype, $entityname, $entityid, $itemstack, $x, $y, $z)";

            cmd.Parameters.AddWithValue("$timestamp", timestamp);
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
        });
    }

    public void CheckEntityLog(int pageNum, IServerPlayer player, int groupId, int x, int y, int z, int radius) {
        System.Threading.Tasks.Task.Run(() => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();
            int skipLogsNum = logLimit * (pageNum - 1);

            using var cmd = connection.CreateCommand();
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

            var logs = new List<string>();
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    logs.Add("<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>");
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
                        logs.Add(logString);
                    }
                }
                else {
                    logs.Add("No entity logs found.");
                }
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                foreach (var log in logs) {
                    Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, log, EnumChatType.CommandSuccess);
                }
            }, "SendEntityLog");
        });
    }

    public void AddContainerLog(string playername, string playeruid, string actiontype, string containerid, string itemstack, int quantity) {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        databaseTasks.Enqueue((connection) => {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO containerlogs (timestamp, playername, playeruid, containerid, itemstack, quantity, actiontype)
            VALUES ($timestamp, $playername, $playeruid, $containerid, $itemstack, $quantity, $actiontype)";

            cmd.Parameters.AddWithValue("$timestamp", timestamp);
            cmd.Parameters.AddWithValue("$playername", playername);
            cmd.Parameters.AddWithValue("$playeruid", playeruid);
            cmd.Parameters.AddWithValue("$actiontype", actiontype);
            cmd.Parameters.AddWithValue("$containerid", containerid);
            cmd.Parameters.AddWithValue("$itemstack", itemstack);
            cmd.Parameters.AddWithValue("$quantity", quantity);

            cmd.ExecuteNonQuery();
        });
    }

    public void CheckContainerLog(int pageNum, IServerPlayer player, int groupId, string containerid) {
        CheckContainerLog(pageNum, player, groupId, new List<string> { containerid });
    }

    public void CheckContainerLog(int pageNum, IServerPlayer player, int groupId, List<string> containerids) {
        System.Threading.Tasks.Task.Run(() => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();
            int skipLogsNum = logLimit * (pageNum - 1);

            string containerIDsQueryStr = "";
            for (int i = 0; i < containerids.Count; i++) {
                containerIDsQueryStr += "'" + containerids[i] + "'";
                if (i < containerids.Count - 1)
                    containerIDsQueryStr += " OR containerid = ";
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, timestamp, playername, playeruid, containerid, itemstack, quantity, actiontype FROM (SELECT * FROM containerlogs WHERE containerid = " + containerIDsQueryStr +
                " ORDER BY id DESC LIMIT $loglimit OFFSET $skiplognum) ORDER BY id ASC";

            cmd.Parameters.AddWithValue("$loglimit", logLimit);
            cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);

            var logs = new List<string>();
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    logs.Add("<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>");
                    while (reader.Read()) {
                        string timestamp = reader.GetString(1);
                        string logPlayername = reader.GetString(2);
                        string logPlayeruid = reader.GetString(3);
                        string actiontype = reader.IsDBNull(7) ? "TAKEN" : reader.GetString(7);
                        string logContainerid = reader.GetString(4);
                        string itemstack = reader.GetString(5);
                        int quantity = reader.GetInt32(6);

                        string logString = String.Format("<strong><font color=\"#6F88DB\">{0}</font></strong> | <strong>{1}</strong>({2}) {6} {5}x{4} in <strong><font color=\"#9BD1EC\">{3}</font></strong>", timestamp, logPlayername, logPlayeruid, logContainerid, itemstack, quantity, actiontype);
                        logs.Add(logString);
                    }
                }
                else {
                    logs.Add("No container logs found.");
                }
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                foreach (var log in logs) {
                    Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, log, EnumChatType.CommandSuccess);
                }
            }, "SendContainerLog");
        });
    }

    public void Dispose() {
        cancellationTokenSource.Cancel();
        workerThread.Join(5000); // Wait up to 5 seconds for worker to finish
        cancellationTokenSource.Dispose();
    }
}