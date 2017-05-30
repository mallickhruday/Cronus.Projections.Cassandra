﻿using Elders.Cronus.DomainModeling;
using Elders.Cronus.Projections.Cassandra.EventSourcing;
using System;

namespace Elders.Cronus.Projections.Cassandra.Snapshots
{
    public class Snapshot : ISnapshot
    {
        public Snapshot(IBlobId id, Type projectionType, object state, int revision)
        {
            Id = id;
            ProjectionType = projectionType;
            State = state;
            Revision = revision;
        }

        public Type ProjectionType { get; private set; }

        public object State { get; set; }

        public int Revision { get; private set; }

        public IBlobId Id { get; private set; }

        public void InitializeState(object state)
        {
            State = state;
        }
    }
}