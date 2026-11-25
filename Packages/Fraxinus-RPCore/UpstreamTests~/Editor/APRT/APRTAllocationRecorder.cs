using System;
using NUnit.Framework;
using UnityEngine.Profiling;

namespace UnityEngine.Rendering.Tests
{
    // Unity's Boehm runtime may return zero from GC.GetAllocatedBytesForCurrentThread.
    // Validate the engine's allocation recorder before accepting a zero-allocation result.
    internal sealed class APRTAllocationRecorder : IDisposable
    {
        static object s_PositiveControl;
        readonly Recorder m_Recorder;

        internal APRTAllocationRecorder()
        {
            m_Recorder = Recorder.Get("GC.Alloc");
            Assert.That(m_Recorder.isValid, Is.True, "GC.Alloc recording must be available.");
            m_Recorder.enabled = false;
            m_Recorder.FilterToCurrentThread();
            try
            {
                Begin();
                s_PositiveControl = new byte[1024];
                int allocations = End();
                Assert.That(allocations, Is.GreaterThan(0), "GC recording must detect the positive control.");
            }
            catch
            {
                Dispose();
                throw;
            }
            finally { s_PositiveControl = null; }
        }

        internal void Begin() => m_Recorder.enabled = true;

        internal int End()
        {
            m_Recorder.enabled = false;
            return m_Recorder.sampleBlockCount;
        }

        public void Dispose()
        {
            m_Recorder.enabled = false;
            m_Recorder.CollectFromAllThreads();
        }
    }
}
