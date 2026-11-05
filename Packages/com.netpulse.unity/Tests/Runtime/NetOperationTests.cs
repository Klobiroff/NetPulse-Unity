using NUnit.Framework;

namespace NetPulse.Tests
{
    public sealed class NetOperationTests
    {
        [Test]
        public void Complete_MarksDoneAndRaisesCompleted()
        {
            var op = new NetOperation();
            var raised = 0;
            op.Completed += _ => raised++;
            Assert.IsFalse(op.IsDone);
            Assert.IsTrue(op.keepWaiting);

            op.Complete();

            Assert.IsTrue(op.IsDone);
            Assert.IsTrue(op.IsSuccess);
            Assert.IsFalse(op.keepWaiting);
            Assert.AreEqual(1, raised);
        }

        [Test]
        public void Completed_AfterDone_InvokesImmediately()
        {
            var op = NetOperation.Succeeded();
            var raised = false;
            op.Completed += _ => raised = true;
            Assert.IsTrue(raised);
        }

        [Test]
        public void Fail_SetsErrorAndDoesNotCompleteTwice()
        {
            var op = new NetOperation();
            var raised = 0;
            op.Completed += _ => raised++;

            op.Fail(-11, "full");
            op.Complete();

            Assert.IsTrue(op.HasError);
            Assert.IsFalse(op.IsSuccess);
            Assert.AreEqual(-11, op.Error.ReturnCode);
            Assert.AreEqual("full", op.Error.Message);
            Assert.AreEqual(1, raised);
        }

        [Test]
        public void Generic_CarriesResult()
        {
            var op = new NetOperation<int>();
            op.Complete(42);
            Assert.AreEqual(42, op.Result);
            Assert.AreEqual(42, op.GetAwaiter().GetResult());
        }

        [Test]
        public void Awaiter_RunsContinuationOnCompletion()
        {
            var op = new NetOperation<string>();
            var awaiter = op.GetAwaiter();
            var continued = false;
            Assert.IsFalse(awaiter.IsCompleted);
            awaiter.OnCompleted(() => continued = true);

            op.Complete("done");

            Assert.IsTrue(continued);
            Assert.IsTrue(awaiter.IsCompleted);
            Assert.AreEqual("done", awaiter.GetResult());
        }

        [Test]
        public void Awaiter_GetResultThrowsNetPulseExceptionOnError()
        {
            var op = NetOperation<int>.Failed(-13, "exists");
            var exception = Assert.Throws<NetPulseException>(() => op.GetAwaiter().GetResult());
            Assert.AreEqual(-13, exception.ReturnCode);
            Assert.AreEqual("exists", exception.Message);
        }

        [Test]
        public void NonGenericAwaiter_ThrowsOnError()
        {
            var op = NetOperation.Failed(-1, "boom");
            Assert.Throws<NetPulseException>(() => op.GetAwaiter().GetResult());
        }
    }
}
