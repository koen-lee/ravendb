﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations;
using Raven.Server.Config.Settings;
using Raven.Server.ServerWide;
using Raven.Server.Utils;
using Sparrow;
using Voron;
using Voron.Exceptions;
using Voron.Impl;
using Voron.Impl.Compaction;

namespace Raven.Server.Documents
{
    public class CompactDatabaseTask
    {
        private readonly ServerStore _serverStore;
        private readonly string _database;
        private CancellationToken _token;

        public CompactDatabaseTask(ServerStore serverStore, string database, CancellationToken token)
        {
            _serverStore = serverStore;
            _database = database;
            _token = token;
        }

        public async Task<IOperationResult> Execute(Action<IOperationProgress> onProgress)
        {
            var progress = new DatabaseCompactionProgress
            {
                Message = $"Started database compaction for {_database}"
            };
            onProgress?.Invoke(progress);
            DatabaseCompactionResult.Instance.SizeBeforeCompactionInMb = await CalculateStorageSizeInBytes(_database) / 1024 / 1024;

            using (await _serverStore.DatabasesLandlord.UnloadAndLockDatabase(_database))
            {
                var configuration = _serverStore.DatabasesLandlord.CreateDatabaseConfiguration(_database);

                using (var src = DocumentsStorage.GetStorageEnvironmentOptionsFromConfiguration(configuration, new IoChangesNotifications(),
                    new CatastrophicFailureNotification(exception => throw new InvalidOperationException($"Failed to compact database {_database}", exception))))
                {
                    var basePath = configuration.Core.DataDirectory.FullPath;
                    IOExtensions.DeleteDirectory(basePath + "-Compacting");
                    IOExtensions.DeleteDirectory(basePath + "-old");
                    try
                    {

                        configuration.Core.DataDirectory = new PathSetting(basePath + "-Compacting");
                        using (var dst = DocumentsStorage.GetStorageEnvironmentOptionsFromConfiguration(configuration, new IoChangesNotifications(),
                            new CatastrophicFailureNotification(exception => throw new InvalidOperationException($"Failed to compact database {_database}", exception))))
                        {
                            _token.ThrowIfCancellationRequested();
                            StorageCompaction.Execute(src, (StorageEnvironmentOptions.DirectoryStorageEnvironmentOptions)dst, progressReport =>
                            {
                                progress.Processed = progressReport.GlobalProgress;
                                progress.Total = progressReport.GlobalTotal;
                                progress.TreeProgress = progressReport.TreeProgress;
                                progress.TreeTotal = progressReport.TreeTotal;
                                progress.TreeName = progressReport.TreeName;
                                progress.Message = progressReport.Message;
                                onProgress?.Invoke(progress);
                            }, _token);
                        }

                        _token.ThrowIfCancellationRequested();
                        IOExtensions.MoveDirectory(basePath, basePath + "-old");
                        IOExtensions.MoveDirectory(basePath + "-Compacting", basePath);
                    }
                    catch (Exception e)
                    {
                        throw new InvalidOperationException($"Failed to execute compaction for {_database}", e);
                    }
                    finally
                    {
                        IOExtensions.DeleteDirectory(basePath + "-Compacting");
                        IOExtensions.DeleteDirectory(basePath + "-old");
                    }
                }
            }

            DatabaseCompactionResult.Instance.SizeAfterCompactionInMb = await CalculateStorageSizeInBytes(_database) / 1024 / 1024;
            return DatabaseCompactionResult.Instance;
        }

        public async Task<long> CalculateStorageSizeInBytes(string databaseName)
        {
            long sizeOnDiskInBytes = 0;

            var database = await _serverStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
            var storageEnvironments = database?.GetAllStoragesEnvironment();
            if (storageEnvironments != null)
            {
                foreach (var environment in storageEnvironments)
                {
                    Transaction tx = null;
                    try
                    {
                        try
                        {
                            tx = environment?.Environment.ReadTransaction();
                        }
                        catch (OperationCanceledException)
                        {
                            continue;
                        }
                        var storageReport = environment?.Environment.GenerateReport(tx);
                        if (storageReport == null)
                            continue;

                        var journalSize = storageReport.Journals.Sum(j => j.AllocatedSpaceInBytes);
                        sizeOnDiskInBytes += storageReport.DataFile.AllocatedSpaceInBytes + journalSize;
                    }
                    finally
                    {
                        tx?.Dispose();
                    }
                }
            }
            return sizeOnDiskInBytes;
        }
    }
}
