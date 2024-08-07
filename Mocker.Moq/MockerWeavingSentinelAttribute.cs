using System;

namespace Mocker.API
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public class MockerWeavingSentinelAttribute : Attribute
    {
        public MockerWeavingSentinelAttribute() : base()
        {
            
        }
    }
}
