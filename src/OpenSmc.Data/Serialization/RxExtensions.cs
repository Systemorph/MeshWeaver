using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenSmc.Data.Serialization
{
    public static class RxExtensions
    {
        public static IObservable<T> MinimumInterval<T>(this IObservable<T> source, TimeSpan interval)
        {
            return Observable.Create<T>(observer =>
            {
                var lastEventTime = DateTimeOffset.MinValue;

                return source.Subscribe(
                    item =>
                    {
                        var now = DateTimeOffset.Now;
                        var elapsed = now - lastEventTime;

                        if (elapsed >= interval)
                        {
                            observer.OnNext(item);
                            lastEventTime = now;
                        }
                    },
                    observer.OnError,
                    observer.OnCompleted
                );
            });
        }
    }
}
