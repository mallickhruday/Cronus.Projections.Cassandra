﻿using System;
using Cassandra;
using Elders.Cronus.Projections.Cassandra.ReplicationStrategies;
using Microsoft.Extensions.Options;

namespace Elders.Cronus.Projections.Cassandra
{
    public class CassandraProvider : ICassandraProvider
    {
        private bool optionsHasChanged = true;

        protected CassandraProviderOptions options;

        protected readonly IKeyspaceNamingStrategy keyspaceNamingStrategy;
        protected readonly ICassandraReplicationStrategy replicationStrategy;
        protected readonly IInitializer initializer;

        protected Cluster cluster;
        protected ISession session;

        private string baseConfigurationKeyspace;

        public CassandraProvider(IOptionsMonitor<CassandraProviderOptions> optionsMonitor, IKeyspaceNamingStrategy keyspaceNamingStrategy, ICassandraReplicationStrategy replicationStrategy, IInitializer initializer = null)
        {
            if (optionsMonitor is null) throw new ArgumentNullException(nameof(optionsMonitor));
            if (keyspaceNamingStrategy is null) throw new ArgumentNullException(nameof(keyspaceNamingStrategy));
            if (replicationStrategy is null) throw new ArgumentNullException(nameof(replicationStrategy));

            this.options = optionsMonitor.CurrentValue;
            optionsMonitor.OnChange(Changed);

            this.keyspaceNamingStrategy = keyspaceNamingStrategy;
            this.replicationStrategy = replicationStrategy;
            this.initializer = initializer;
        }

        public Cluster GetCluster()
        {
            if (cluster is null == false && optionsHasChanged == false)
                return cluster;

            Builder builder = initializer as Builder;
            if (builder is null)
            {
                builder = Cluster.Builder();
                //  TODO: check inside the `cfg` (var cfg = builder.GetConfiguration();) if we already have connectionString specified

                string connectionString = options.ConnectionString;

                var hackyBuilder = new CassandraConnectionStringBuilder(connectionString);
                if (string.IsNullOrEmpty(hackyBuilder.DefaultKeyspace) == false)
                    connectionString = connectionString.Replace(hackyBuilder.DefaultKeyspace, string.Empty);
                baseConfigurationKeyspace = hackyBuilder.DefaultKeyspace;

                var connStrBuilder = new CassandraConnectionStringBuilder(connectionString);
                cluster = connStrBuilder
                    .ApplyToBuilder(builder)
                    .WithReconnectionPolicy(new ExponentialReconnectionPolicy(100, 100000))
                    .WithRetryPolicy(new NoHintedHandOffRetryPolicy())
                    .Build();
            }

            else
            {
                cluster = Cluster.BuildFrom(initializer);
            }

            optionsHasChanged = false;

            return cluster;
        }

        protected virtual string GetKeyspace()
        {
            return keyspaceNamingStrategy.GetName(baseConfigurationKeyspace).ToLower();
        }

        public ISession GetSession()
        {
            if (session is null || session.IsDisposed || optionsHasChanged)
            {
                session = GetCluster().Connect();
                CreateKeyspace(GetKeyspace(), replicationStrategy);
            }

            return session;
        }

        private void CreateKeyspace(string keyspace, ICassandraReplicationStrategy replicationStrategy)
        {
            var createKeySpaceQuery = replicationStrategy.CreateKeySpaceTemplate(keyspace);
            session.Execute(createKeySpaceQuery);
            session.ChangeKeyspace(keyspace);
        }

        private void Changed(CassandraProviderOptions newOptions)
        {
            options = newOptions;
            optionsHasChanged = true;
        }
    }
}
