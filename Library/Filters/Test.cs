﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BlendInteractive.Denina.Core.Documentation;
using DeninaSharp.Core;
using DeninaSharp.Core.Documentation;

namespace DeninaSharp.Core.Filters
{
    [Filters("Test", "Filters for unit testing. These will not be written to the documentation.")]
    public class Test
    {
        [Filter("FakeTest", "This is a fake test which requires a fake class, used to test dependency checking")]
        [Requires("SomeFakeClass", "This class doesn't exist.")]
        public static string FakeTest(string input, PipelineCommand command)
        {
            return input;
        }

    }
}
