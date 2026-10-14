using System;
using System.Threading;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils
{
    public sealed class RetainedCacheTests
    {
        [Fact]
        public void SameKeyReturnsSameValueAndPeekDoesNotCreate()
        {
            int creates = 0;
            using var cache = new RetainedCache<string, object>(_ => { }, TimeSpan.FromSeconds(1));

            Assert.False(cache.TryPeek("map11", out _));
            object first = cache.Get("map11", () =>
            {
                creates++;
                return new object();
            });
            object second = cache.Get("map11", () =>
            {
                creates++;
                return new object();
            });

            Assert.Equal(1, creates);
            Assert.Same(first, second);
            Assert.True(cache.TryPeek("map11", out object peeked));
            Assert.Same(first, peeked);
        }

        [Fact]
        public void HeldValueOutlivesGraceAndExpiresOnlyAfterRelease()
        {
            using var disposed = new ManualResetEventSlim();
            using var cache = new RetainedCache<string, object>(
                _ => disposed.Set(),
                TimeSpan.FromMilliseconds(40));
            cache.Get("map11", () => new object());
            Action release = cache.Hold("map11");

            Assert.False(disposed.Wait(120));
            release();
            release();

            Assert.True(disposed.Wait(1000));
            Assert.False(cache.TryPeek("map11", out _));
        }

        [Fact]
        public void UnheldCreatedValueExpiresAfterGrace()
        {
            using var disposed = new ManualResetEventSlim();
            using var cache = new RetainedCache<string, object>(
                _ => disposed.Set(),
                TimeSpan.FromMilliseconds(30));

            cache.Get("discarded-render", () => new object());

            Assert.True(disposed.Wait(1000));
            Assert.False(cache.TryPeek("discarded-render", out _));
        }
    }
}
