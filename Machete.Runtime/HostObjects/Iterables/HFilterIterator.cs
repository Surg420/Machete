﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Machete.Core;

namespace Machete.Runtime.HostObjects.Iterables
{
    public sealed class HFilterIterator : HIteratorBase
    {
        private readonly Iterator _iterator;
        private readonly ICallable _predicate;
        private bool _initialized;
        private bool _complete;
        
        public HFilterIterator(IEnvironment environment, Iterator iterator, ICallable predicate)
            : base(environment)
        {
            _iterator = iterator;
            _predicate = predicate;
        }

        public override IDynamic Current(IEnvironment environment, IArgs args)
        {
            if (!_initialized)
                throw environment.CreateTypeError("");
            return _iterator.Current;
        }

        public override IDynamic Next(IEnvironment environment, IArgs args)
        {
            if (_complete)
                return environment.False;
            _initialized = true;
            while (_iterator.Next())
            {
                var callArgs = environment.CreateArgs(new[] { _iterator.Current });
                if (_predicate.Call(environment, environment.Undefined, callArgs).ConvertToBoolean().BaseValue)
                    return environment.True;
            }
            _complete = true;
            return environment.False;
        }
    }
}