package com.iconnect.proof

import android.system.Os
import java.io.FileDescriptor

/** Android streams constructed from an existing descriptor borrow it; Os.open's caller owns it. */
internal inline fun <T> FileDescriptor.useOsDescriptor(action:(FileDescriptor)->T):T =
    try {action(this)} finally {if(valid()) Os.close(this)}
