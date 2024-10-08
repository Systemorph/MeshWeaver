/******************************************************************************************************
 * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
 ******************************************************************************************************/

namespace MeshWeaver.Collections
{
    public interface IObjectStore<in TKey, out TValue>
    {
        TValue GetInstance(TKey key);
    }
}