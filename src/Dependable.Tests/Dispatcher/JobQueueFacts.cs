using System;
using System.Linq;
using System.Threading.Tasks;
using Dependable.Dispatcher;
using NSubstitute;
using Xunit;

namespace Dependable.Tests.Dispatcher
{
    public class JobQueueFacts
    {
        public class Initialize
        {
            readonly World _world = new World();
            readonly Job _matchingJob;
            readonly Job _otherJob;
            readonly ActivityConfiguration _throttledConfiguration = new ActivityConfiguration(typeof(string));
            readonly ActivityConfiguration _defaultConfiguration = new ActivityConfiguration();

            public Initialize()
            {
                _matchingJob = _world.NewJob.OfType<string>().In(JobStatus.Ready);
                _otherJob = _world.NewJob.OfType<int>().In(JobStatus.Ready);
                _throttledConfiguration.WithMaxQueueLength(1);
            }

            [Fact]
            public async Task ShouldEnqueueMatchingItems()
            {
                var q = _world.NewJobQueue(_throttledConfiguration);
                var next = _world.NewJob.OfType<string>().In(JobStatus.Ready);

                q.Initialize(new[] { _matchingJob, _otherJob });
                
                Assert.Equal(_matchingJob, await q.Read());

                q.Write(next);
                Assert.Equal(next, await q.Read());
            }

            [Fact]
            public async Task ShouldNotEnqueueMoreItemsThanMaxQueueLength()
            {
                var q = _world.NewJobQueue(_throttledConfiguration);
                var extra = _world.NewJob.OfType<string>().In(JobStatus.Ready);
                var next = _world.NewJob.OfType<string>().In(JobStatus.Ready);

                q.Initialize(new[] { _matchingJob, extra, _otherJob });

                Assert.Equal(_matchingJob, await q.Read());

                q.Write(next);
                Assert.Equal(next, await q.Read());
            }

            [Fact]
            public void ShouldNotReturnMatchingItemsEvenIfTheyWereNotConsumed()
            {
                var q = _world.NewJobQueue(_throttledConfiguration);
                var extra = _world.NewJob.OfType<string>().In(JobStatus.Ready);

                var rest = q.Initialize(new[] { _matchingJob, extra, _otherJob });

                Assert.Equal(_otherJob, rest.Single());
            }

            [Fact]
            public async Task ShouldEnqueueAllItemsForDefaultQueue()
            {
                var q = _world.NewJobQueue(_defaultConfiguration);
                var extra = _world.NewJob.OfType<string>().In(JobStatus.Ready);

                var rest = q.Initialize(new[] { _matchingJob, extra });

                Assert.Equal(_matchingJob, await q.Read());
                Assert.Equal(extra, await q.Read());
                Assert.Empty(rest);
            }

            [Fact]
            public void ShouldReturnUnconsumedItems()
            {
                var q = _world.NewJobQueue(_throttledConfiguration);

                var rest = q.Initialize(new[] { _matchingJob, _otherJob });

                Assert.Equal(_otherJob, rest.Single());
            }

            [Fact]
            public void ShouldThrowAnExceptionIfItIsAlreadyInitialized()
            {
                var q = _world.NewJobQueue(new ActivityConfiguration(typeof(string)));

                q.Initialize(new Job[0]);

                Assert.Throws<InvalidOperationException>(() => q.Initialize(new Job[0]));
            }

            [Fact]
            public void ShouldInitializeSuspendedCount()
            {
                var q = _world.NewJobQueue(new ActivityConfiguration(typeof(string)));

                q.Initialize(new Job[0]);

                _world.PersistenceStore.Received(1).CountSuspended(typeof(string));
            }
        }

        public class Reads
        {
            readonly World _world = new World();

            [Fact]
            public void ShouldReturnAnAwaitableTaskWhenQueueIsEmpty()
            {
                var t = _world.NewJobQueue().Read();
                Assert.Equal(TaskStatus.WaitingForActivation, t.Status);
            }

            [Fact]
            public async void ShouldReturnTheFirstItemInTheQueue()
            {
                var jobA = _world.NewJob.In(JobStatus.Ready);
                var jobB = _world.NewJob.In(JobStatus.Ready);

                var q = _world.NewJobQueue();
                q.Write(jobA);
                q.Write(jobB);

                Assert.Equal(jobA, await q.Read());
            }

            [Fact]
            public async void ShouldSignalTheAwaitersWhenNewItemsAreAdded()
            {
                var q = _world.NewJobQueue();
                var t = q.Read();

                var job = _world.NewJob;

                q.Write(job);

                Assert.Equal(job, await t);
            }            
        }

        public class ExcessReads
        {
            readonly World _world = new World();
            readonly ActivityConfiguration _configuration = new ActivityConfiguration(typeof (string)).WithMaxQueueLength(1);
            readonly JobQueue _q;
            readonly Job _excess;

            public ExcessReads()
            {
                _q = _world.NewJobQueue(_configuration);

                _excess = _world.NewJob;


                _world.PersistenceStore.LoadSuspended(null, 0).ReturnsForAnyArgs(new[] { _excess });

                _q.Write(_world.NewJob);
                _q.Write(_excess);
            }

            [Fact]
            public async Task ShouldLoadSuspendedItemsAfterTheCurrentBufferIsEmpty()
            {
                await _q.Read();
                Assert.Equal(_excess, await _q.Read());
            }

            [Fact]
            public async Task ShouldOnlyLoadAsMuchAsTheBufferCanHold()
            {
                await _q.Read();
                await _q.Read();

                _world.PersistenceStore.Received(1).LoadSuspended(typeof (string), _configuration.MaxQueueLength);
            }

            [Fact]
            public async Task ShouldUpdateSuspendedFlagAndStore()
            {
                var suspended = true;
                _world.PersistenceStore.When(s => s.Store(_excess)).Do(c => suspended = ((Job) c.Args()[0]).Suspended);

                await _q.Read();
                await _q.Read();

                Assert.False(suspended);
            }

            [Fact]
            public async Task ShouldRetryIfItFailsOnLoadSuspended()
            {
                var error = false;
                _world.PersistenceStore.When(s => s.LoadSuspended(typeof (string), _configuration.MaxQueueLength)).Do(
                    c =>
                    {
                        error = !error;
                        if (error) throw new Exception("Doh");
                    });

                await _q.Read();
                                
                Assert.Equal(_excess, await _q.Read());
            }

            [Fact]
            public async Task ShouldIgnoreFailuresOnStore()
            {
                var moreExcess = _world.NewJob;

                _q.Write(moreExcess);

                _world.PersistenceStore
                    .LoadSuspended(typeof (string), _configuration.MaxQueueLength)
                    .Returns(new[] {_excess, moreExcess });

                _world.PersistenceStore.When(s => s.Store(moreExcess)).Do(_ => { throw new Exception("Doh"); });

                await _q.Read();
                
                Assert.Equal(_excess, await _q.Read());
            }
        }

        public class ExcessWrites
        {            
            readonly World _world = new World();

            readonly ActivityConfiguration _configuration = new ActivityConfiguration(typeof (string)).WithMaxQueueLength(1);

            readonly Job _excessItem;

            readonly JobQueue _queue;

            public ExcessWrites()
            {
                _excessItem = _world.NewJob;
                _queue = _world.NewJobQueue(_configuration);
                _queue.Write(_world.NewJob);
            }

            [Fact]
            public void ShouldSuspendExcessItems()
            {
                _queue.Write(_excessItem);
                Assert.True(_excessItem.Suspended);
            }

            [Fact]
            public void ShouldPersistTheSuspendedJob()
            {
                var suspended = false;
                _world.PersistenceStore.When(s => s.Store(_excessItem))
                    .Do(c => suspended = ((Job) c.Args()[0]).Suspended);

                _queue.Write(_excessItem);

                Assert.True(suspended);
                _world.PersistenceStore.Received(1).Store(_excessItem);
            }

            [Fact]
            public void ShouldNotSuspendJobsOnDefaultQueue()
            {
                var configuration = new ActivityConfiguration().WithMaxQueueLength(1);
                var q = _world.NewJobQueue(configuration);

                q.Write(_world.NewJob);
                q.Write(_world.NewJob);

                _world.PersistenceStore.DidNotReceiveWithAnyArgs().Store((Job) null);
            }

            [Fact]
            public async Task ShouldSuspendUntilTheEntireBufferIsProcessed()
            {
                var configuration = new ActivityConfiguration(typeof (string)).WithMaxQueueLength(2);

                var q = _world.NewJobQueue(configuration);

                q.Write(_world.NewJob);
                q.Write(_world.NewJob);

                q.Write(_world.NewJob);

                await q.Read();

                var job = (Job) _world.NewJob;
                q.Write(job);

                Assert.True(job.Suspended);
            }

            [Fact]
            public async Task ShouldNotSuspendAfterTheBufferIsProcessed()
            {
                var configuration = new ActivityConfiguration(typeof (string)).WithMaxQueueLength(1);

                var q = _world.NewJobQueue(configuration);

                q.Write(_world.NewJob);

                var suspendedJob = _world.NewJob;
                q.Write(suspendedJob);

                _world.PersistenceStore.LoadSuspended(typeof (string), Arg.Any<int>())
                    .Returns(new Job[] {suspendedJob});

                await q.Read();
                await q.Read();

                var job = (Job) _world.NewJob;

                q.Write(job);

                Assert.False(job.Suspended);
            }

            public class Write
            {
                readonly World _world = new World();

                [Fact]
                public async Task ShouldDispatchToTheAwaitingReader()
                {
                    var q = _world.NewJobQueue();
                    var reader = q.Read();
                    var job = _world.NewJob;

                    q.Write(job);

                    var readJob = await reader;

                    Assert.Equal(job, readJob);
                }
            }
        }
    }


    public partial class WorldExtensions
    {
        public static JobQueue NewJobQueue(this World world, ActivityConfiguration configuration = null)
        {
            return new JobQueue(configuration ?? new ActivityConfiguration(), world.PersistenceStore, world.EventStream);
        }
    }
}