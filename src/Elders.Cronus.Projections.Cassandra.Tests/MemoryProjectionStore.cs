﻿using Elders.Cronus.DomainModeling;
using Elders.Cronus.Projections.Cassandra.EventSourcing;
using System.Collections.Generic;
using System.Linq;
using Elders.Cronus.Projections.Cassandra.Snapshots;
using System;

namespace Elders.Cronus.Projections.Cassandra.Tests
{
    public class MemoryProjectionStore : IProjectionStore
    {
        private List<ProjectionCommit> commits;

        public MemoryProjectionStore()
        {
            commits = new List<ProjectionCommit>();
        }

        public void BeginReplay(string projectionContractId)
        {
            throw new NotImplementedException();
        }

        public void EndReplay(string projectionContractId)
        {
            throw new NotImplementedException();
        }

        public ProjectionStream Load(string projectionContractId, IBlobId projectionId, ISnapshot snapshot, bool isReplay)
        {
            return new ProjectionStream(
                commits.Where(x =>
                    x.ProjectionId == projectionId
                    && x.SnapshotMarker > snapshot.Revision).ToList(),
                snapshot);
        }

        public void Save(ProjectionCommit commit, bool isReplay)
        {
            commits.Add(commit);
        }
    }
}
