using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.Layout.DataBinding
{
    public static class ReactiveExtensions
    {
        /// <summary>
        /// Debounces an observable sequence by delaying emissions until a specified time span has elapsed
        /// between emissions. The final value is guaranteed to be emitted. When the timer expires, the value
        /// is only emitted if it's different from the previously emitted one.
        /// </summary>
        /// <typeparam name="T">The type of the elements in the source sequence.</typeparam>
        /// <param name="source">The source observable sequence.</param>
        /// <param name="dueTime">The time span to wait after an emission before emitting the most recent value.</param>
        /// <param name="scheduler">The scheduler to use for timing.</param>
        /// <returns>An observable sequence that debounces the source sequence.</returns>
        public static IObservable<T> Debounce<T>(
            this IObservable<T> source,
            TimeSpan dueTime,
            IScheduler? scheduler = null)
        {
            scheduler ??= DefaultScheduler.Instance;

            return Observable.Create<T>(observer =>
            {
                var serialDisposable = new SerialDisposable();
                var hasValue = false;
                var value = default(T)!;
                var hasEmitted = false;
                var lastEmitted = default(T)!;
                var gate = new object();

                var sourceSubscription = source.Subscribe(
                    onNext: x =>
                    {
                        lock (gate)
                        {
                            hasValue = true;
                            value = x;

                            serialDisposable.Disposable = scheduler.Schedule(dueTime, () =>
                            {
                                lock (gate)
                                {
                                    if (hasValue)
                                    {
                                        if (!hasEmitted || !EqualityComparer<T>.Default.Equals(lastEmitted, value))
                                        {
                                            observer.OnNext(value);
                                            lastEmitted = value;
                                            hasEmitted = true;
                                        }
                                        hasValue = false;
                                    }
                                }
                            });
                        }
                    },
                    onError: observer.OnError,
                    onCompleted: () =>
                    {
                        serialDisposable.Disposable = scheduler.Schedule(() =>
                        {
                            lock (gate)
                            {
                                if (hasValue)
                                {
                                    if (!hasEmitted || !EqualityComparer<T>.Default.Equals(lastEmitted, value))
                                    {
                                        observer.OnNext(value);
                                    }
                                }
                                observer.OnCompleted();
                            }
                        });
                    }
                );

                return new CompositeDisposable(serialDisposable, sourceSubscription);
            });
        }
        /// <summary>
        /// Leading-edge throttle: emits the first value immediately, then suppresses subsequent values
        /// for the specified interval. After the interval expires, if a value was suppressed during
        /// the cooldown, it is emitted (guaranteeing the final state is always delivered).
        /// </summary>
        /// <typeparam name="T">The type of the elements in the source sequence.</typeparam>
        /// <param name="source">The source observable sequence.</param>
        /// <param name="interval">The cooldown interval after each emission.</param>
        /// <param name="scheduler">The scheduler to use for timing.</param>
        /// <returns>An observable sequence with leading-edge throttle behavior.</returns>
        /// <summary>
        /// Leading-edge throttle using the default scheduler.
        /// </summary>
        public static IObservable<T> ThrottleImmediate<T>(
            this IObservable<T> source,
            TimeSpan interval)
            => ThrottleImmediate(source, interval, DefaultScheduler.Instance);

        public static IObservable<T> ThrottleImmediate<T>(
            this IObservable<T> source,
            TimeSpan interval,
            IScheduler scheduler)
        {
            return Observable.Create<T>(observer =>
            {
                var gate = new object();
                var cooldownDisposable = new SerialDisposable();
                var inCooldown = false;
                var hasPending = false;
                var pendingValue = default(T)!;

                var sourceSubscription = source.Subscribe(
                    onNext: x =>
                    {
                        lock (gate)
                        {
                            if (!inCooldown)
                            {
                                // Leading edge: emit immediately
                                observer.OnNext(x);
                                inCooldown = true;
                                hasPending = false;

                                // Schedule end of cooldown
                                cooldownDisposable.Disposable = scheduler.Schedule(interval, () =>
                                {
                                    lock (gate)
                                    {
                                        inCooldown = false;
                                        if (hasPending)
                                        {
                                            // Emit the latest suppressed value and start a new cooldown
                                            hasPending = false;
                                            observer.OnNext(pendingValue);
                                            inCooldown = true;

                                            cooldownDisposable.Disposable = scheduler.Schedule(interval, () =>
                                            {
                                                lock (gate)
                                                {
                                                    inCooldown = false;
                                                    if (hasPending)
                                                    {
                                                        hasPending = false;
                                                        observer.OnNext(pendingValue);
                                                    }
                                                }
                                            });
                                        }
                                    }
                                });
                            }
                            else
                            {
                                // In cooldown: remember the latest value
                                hasPending = true;
                                pendingValue = x;
                            }
                        }
                    },
                    onError: observer.OnError,
                    onCompleted: () =>
                    {
                        lock (gate)
                        {
                            if (hasPending)
                            {
                                observer.OnNext(pendingValue);
                                hasPending = false;
                            }
                            observer.OnCompleted();
                        }
                    }
                );

                return new CompositeDisposable(cooldownDisposable, sourceSubscription);
            });
        }
    }
}
