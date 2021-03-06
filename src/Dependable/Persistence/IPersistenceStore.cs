﻿using System;
using System.Collections.Generic;

namespace Dependable.Persistence
{
    public interface IPersistenceStore : IDisposable
    {
        Job Load(Guid id);

        Job LoadBy(Guid correlationId);

        IEnumerable<Job> LoadBy(JobStatus status);

        void Store(Job job);

        void Store(IEnumerable<Job> jobs);

        IEnumerable<Job> LoadSuspended(Type forActivityType, int max);

        IEnumerable<Job> LoadSuspended(IEnumerable<Type> excludeActivityTypes, int max);

        int CountSuspended(Type type);
    }
}