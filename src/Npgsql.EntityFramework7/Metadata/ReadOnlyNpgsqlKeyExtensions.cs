﻿using JetBrains.Annotations;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Relational.Metadata;

namespace EntityFramework.Npgsql.Metadata
{
    public class ReadOnlyNpgsqlKeyExtensions : ReadOnlyRelationalKeyExtensions, INpgsqlKeyExtensions
    {
        protected const string NpgsqlNameAnnotation = NpgsqlAnnotationNames.Prefix + RelationalAnnotationNames.Name;
        protected const string NpgsqlClusteredAnnotation = NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.Clustered;

        public ReadOnlyNpgsqlKeyExtensions([NotNull] IKey key)
            : base(key)
        {
        }

        public override string Name
            => Key[NpgsqlNameAnnotation] as string
               ?? base.Name;

        public virtual bool? IsClustered
        {
            get
            {
                // TODO: Issue #777: Non-string annotations
                // TODO: Issue #700: Annotate associated index object instead
                var value = Key[NpgsqlClusteredAnnotation] as string;
                return value == null ? null : (bool?)bool.Parse(value);
            }
        }
    }
}