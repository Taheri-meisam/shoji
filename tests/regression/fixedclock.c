// Freezes the wall clock for the theme regression screenshots (scripts/theme-regression.sh).
//
// LD_PRELOAD=fixedclock.so SHOJI_FAKE_EPOCH=<unix seconds> makes CLOCK_REALTIME (and gettimeofday / time) always
// answer that instant, so every DateTime.Now in Shoji — clock tiles, calendars, "5 min ago", a backdrop's date —
// agrees with the demo snapshot. The monotonic clocks are left alone: timers, Task.Delay and Stopwatch keep working.
// Build: gcc -shared -fPIC -O2 -o fixedclock.so fixedclock.c
#define _GNU_SOURCE
#include <stdlib.h>
#include <time.h>
#include <sys/time.h>
#include <sys/syscall.h>
#include <unistd.h>

static time_t fixed_epoch(void)
{
    static time_t epoch = -1;
    if (epoch == -1)
    {
        const char *v = getenv("SHOJI_FAKE_EPOCH");
        epoch = v ? (time_t)strtoll(v, NULL, 10) : 0;
    }
    return epoch;
}

int clock_gettime(clockid_t id, struct timespec *ts)
{
    if ((id == CLOCK_REALTIME || id == CLOCK_REALTIME_COARSE) && fixed_epoch() > 0)
    {
        ts->tv_sec = fixed_epoch();
        ts->tv_nsec = 0;
        return 0;
    }
    return (int)syscall(SYS_clock_gettime, id, ts);
}

int gettimeofday(struct timeval *tv, void *tz)
{
    (void)tz;
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    tv->tv_sec = ts.tv_sec; // glibc declares tv non-null
    tv->tv_usec = ts.tv_nsec / 1000;
    return 0;
}

time_t time(time_t *out)
{
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    if (out)
        *out = ts.tv_sec;
    return ts.tv_sec;
}
