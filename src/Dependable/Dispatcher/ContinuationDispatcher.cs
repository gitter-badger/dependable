using System;
using System.Collections.Generic;
using System.Linq;
using Dependable.Persistence;
using Dependable.Recovery;

namespace Dependable.Dispatcher
{
    public interface IContinuationDispatcher
    {
        Continuation[] Dispatch(Job job);
    }

    /// <summary>
    /// Used to evaluate to continuation of a given job and 
    /// dispatch the appropriate jobs.
    /// </summary>
    public class ContinuationDispatcher : IContinuationDispatcher
    {
        readonly IJobRouter _router;
        readonly IJobMutator _jobMutator;
        readonly IPersistenceStore _persistenceStore;
        readonly IRecoverableAction _recoverableAction;

        public ContinuationDispatcher(IJobRouter router,
            IJobMutator jobMutator,
            IPersistenceStore persistenceStore,
            IRecoverableAction recoverableAction)
        {
            if (router == null) throw new ArgumentNullException("router");
            if (jobMutator == null) throw new ArgumentNullException("JobMutator");
            if (persistenceStore == null) throw new ArgumentNullException("persistenceStore");
            if (recoverableAction == null) throw new ArgumentNullException("recoverableAction");

            _router = router;
            _jobMutator = jobMutator;
            _persistenceStore = persistenceStore;
            _recoverableAction = recoverableAction;
        }

        public Continuation[] Dispatch(Job job)
        {
            if (job == null) throw new ArgumentNullException("job");

            var readyContinuations = job.Continuation.PendingContinuations().ToArray();

            foreach (var continuation in readyContinuations)
                continuation.Status = JobStatus.Ready;

            _jobMutator.Mutate<ContinuationDispatcher>(job, continuation: job.Continuation);

            DispatchCore(readyContinuations);

            return readyContinuations;
        }

        /// <summary>
        /// Finds all schedulable jobs that are in 
        /// Created state. Then for each of them, 
        /// attempts to update the status to Ready.
        /// Once successful, routes the job.
        /// </summary>
        void DispatchCore(IEnumerable<Continuation> readyContinuations)
        {
            var schedulableJobs = (
                from @await in readyContinuations
                let j = _persistenceStore.Load(@await.Id)
                where j.Status == JobStatus.Created
                select j)
                .ToArray();

            foreach (var job in schedulableJobs)
            {
                var jobReference = job;
                _recoverableAction.Run(
                    () => jobReference = _jobMutator.Mutate<ContinuationDispatcher>(jobReference, JobStatus.Ready),
                    then: () => _router.Route(jobReference));
            }
        }
    }
}