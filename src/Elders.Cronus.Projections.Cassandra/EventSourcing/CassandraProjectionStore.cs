﻿using System.Collections.Generic;
using System;
using Cassandra;
using System.Collections.Concurrent;
using Elders.Cronus.Serializer;
using System.IO;
using System.Linq;
using Elders.Cronus.Projections.Cassandra.Logging;

namespace Elders.Cronus.Projections.Cassandra.EventSourcing
{
    public class CassandraProjectionStore : IProjectionStore
    {
        static ILog log = LogProvider.GetLogger(typeof(CassandraProjectionStore));

        static readonly object createMutex = new object();
        static readonly object dropMutex = new object();

        public readonly string CreateProjectionEventsTableTemplate = @"CREATE TABLE IF NOT EXISTS ""{0}"" (id text, sm int, evarid text, evarrev int, evarts bigint, evarpos int, data blob, PRIMARY KEY ((id, sm), evarid, evarrev, evarpos, evarts)) WITH CLUSTERING ORDER BY (evarid ASC);";
        const string InsertQueryTemplate = @"INSERT INTO ""{0}"" (id, sm, evarid, evarrev, evarpos, evarts, data) VALUES (?,?,?,?,?,?,?);";
        const string GetQueryTemplate = @"SELECT data FROM ""{0}"" WHERE id=? AND sm=?;";
        const string DropQueryTemplate = @"DROP TABLE IF EXISTS ""{0}"";";

        readonly ISession session;
        readonly ConcurrentDictionary<string, PreparedStatement> SavePreparedStatements;
        readonly ConcurrentDictionary<string, PreparedStatement> GetPreparedStatements;
        readonly ConcurrentDictionary<string, PreparedStatement> CreatePreparedStatements;
        readonly ConcurrentDictionary<string, PreparedStatement> DropPreparedStatements;
        readonly ISerializer serializer;
        readonly IProjectionVersionResolver projectionVersionResolver;

        public CassandraProjectionStore(IEnumerable<Type> projections, ISession session, ISerializer serializer, IProjectionVersionResolver projectionVersionResolver)
        {
            if (ReferenceEquals(null, projections) == true) throw new ArgumentNullException(nameof(projections));
            if (ReferenceEquals(null, session) == true) throw new ArgumentNullException(nameof(session));
            if (ReferenceEquals(null, serializer) == true) throw new ArgumentNullException(nameof(serializer));
            if (ReferenceEquals(null, projectionVersionResolver) == true) throw new ArgumentNullException(nameof(projectionVersionResolver));

            this.serializer = serializer;
            this.session = session;
            this.projectionVersionResolver = projectionVersionResolver;
            this.SavePreparedStatements = new ConcurrentDictionary<string, PreparedStatement>();
            this.GetPreparedStatements = new ConcurrentDictionary<string, PreparedStatement>();
            this.CreatePreparedStatements = new ConcurrentDictionary<string, PreparedStatement>();
            this.DropPreparedStatements = new ConcurrentDictionary<string, PreparedStatement>();
            InitializeProjectionDatabase(projections);
        }

        public ProjectionStream Load(string contractId, IBlobId projectionId, ISnapshot snapshot)
        {
            var version = projectionVersionResolver.GetVersions(contractId).Where(x => x.Status == ProjectionStatus.Live).Single();
            return Load(contractId, projectionId, snapshot, version.ProjectionName + "_" + version.VersionNumber);
        }

        ProjectionStream Load(string contractId, IBlobId projectionId, ISnapshot snapshot, string columnFamily)
        {
            string projId = Convert.ToBase64String(projectionId.RawId);
            List<ProjectionCommit> commits = new List<ProjectionCommit>();
            bool tryGetRecords = true;
            int snapshotMarker = snapshot.Revision + 1;

            while (tryGetRecords)
            {
                tryGetRecords = false;
                BoundStatement bs = GetPreparedStatementToGetProjection(columnFamily).Bind(projId, snapshotMarker);
                var result = session.Execute(bs);
                var rows = result.GetRows();
                foreach (var row in rows)
                {
                    tryGetRecords = true;
                    var data = row.GetValue<byte[]>("data");
                    using (var stream = new MemoryStream(data))
                    {
                        commits.Add((ProjectionCommit)serializer.Deserialize(stream));
                    }
                }
                snapshotMarker++;
            }

            if (commits.Count > 1000)
                log.Warn($"Potential memory leak. The system will be down fairly soon. The projection `{contractId}` for id={projectionId} loads a lot of projection commits ({commits.Count}) and snapshot `{snapshot.GetType().Name}` which puts a lot of CPU and RAM pressure. You can resolve this by enabling the Snapshots feature in the host which handles projection WRITES and READS using `.UseSnapshots(...)`.");

            return new ProjectionStream(projectionId, commits, snapshot);
        }

        public void Save(ProjectionCommit commit)
        {
            string projectionCommitLocationBasedOnVersion = commit.Version.ProjectionName + "_" + commit.Version.VersionNumber;
            Save(commit, projectionCommitLocationBasedOnVersion);
        }

        void Save(ProjectionCommit commit, string columnFamily)
        {
            var data = serializer.SerializeToBytes(commit);
            var statement = SavePreparedStatements.GetOrAdd(columnFamily, x => BuildInsertPreparedStatemnt(x));
            var result = session.Execute(statement
                .Bind(
                    ConvertIdToString(commit.ProjectionId),
                    commit.SnapshotMarker,
                    commit.EventOrigin.AggregateRootId,
                    commit.EventOrigin.AggregateRevision,
                    commit.EventOrigin.AggregateEventPosition,
                    commit.EventOrigin.Timestamp,
                    data
                ));
        }

        public void DropTable(string location)
        {
            // https://issues.apache.org/jira/browse/CASSANDRA-10699
            // https://issues.apache.org/jira/browse/CASSANDRA-11429
            lock (dropMutex)
            {
                var statement = CreatePreparedStatements.GetOrAdd(location, x => BuildDropPreparedStatemnt(x));
                statement.SetConsistencyLevel(ConsistencyLevel.All);
                session.Execute(statement.Bind());
            }
        }

        public void CreateTable(string template, string location)
        {
            // https://issues.apache.org/jira/browse/CASSANDRA-10699
            // https://issues.apache.org/jira/browse/CASSANDRA-11429
            lock (createMutex)
            {
                var statement = CreatePreparedStatements.GetOrAdd(location, x => BuildCreatePreparedStatemnt(template, x));
                statement.SetConsistencyLevel(ConsistencyLevel.All);
                session.Execute(statement.Bind());
            }
        }

        PreparedStatement BuildInsertPreparedStatemnt(string columnFamily)
        {
            return session.Prepare(string.Format(InsertQueryTemplate, columnFamily));
        }

        PreparedStatement BuildDropPreparedStatemnt(string columnFamily)
        {
            return session.Prepare(string.Format(DropQueryTemplate, columnFamily));
        }

        PreparedStatement BuildCreatePreparedStatemnt(string template, string columnFamily)
        {
            return session.Prepare(string.Format(template, columnFamily));
        }

        void InitializeProjectionDatabase(IEnumerable<Type> projections)
        {
            foreach (var projType in projections
                .Where(x => typeof(IProjectionDefinition).IsAssignableFrom(x))
                .Where(x => x.GetInterfaces().Any(y => y.IsGenericType && y.GetGenericTypeDefinition() == typeof(IEventHandler<>))))
            {
                var versions = projectionVersionResolver.GetVersions(projType);
                CreateTable(CreateProjectionEventsTableTemplate, versions.GetLiveColumnFamily());
            }
        }

        PreparedStatement GetPreparedStatementToGetProjection(string columnFamily)
        {
            PreparedStatement loadAggregatePreparedStatement;
            if (!GetPreparedStatements.TryGetValue(columnFamily, out loadAggregatePreparedStatement))
            {
                loadAggregatePreparedStatement = session.Prepare(string.Format(GetQueryTemplate, columnFamily));
                GetPreparedStatements.TryAdd(columnFamily, loadAggregatePreparedStatement);
            }
            return loadAggregatePreparedStatement;
        }

        string ConvertIdToString(object id)
        {
            if (id is string || id is Guid)
                return id.ToString();

            if (id is IBlobId)
            {
                return Convert.ToBase64String((id as IBlobId).RawId);
            }
            throw new NotImplementedException(String.Format("Unknow type id {0}", id.GetType()));
        }

        public IProjectionBuilder GetBuilder(Type projectionType)
        {
            return null;
            //return new EventSourcedProjectionBuilder(this, projectionType.GetContractId(), versionStore);
        }
    }

    public static class ProjectionVersionExtensions
    {
        public static string GetColumnFamily(this ProjectionVersion version)
        {
            return version.ProjectionName + "_" + version.VersionNumber;
        }

        public static string GetLiveColumnFamily(this IEnumerable<ProjectionVersion> versions)
        {
            return GetColumnFamily(versions.Where(ver => ver.Status == ProjectionStatus.Live).Single());
        }
    }
}
