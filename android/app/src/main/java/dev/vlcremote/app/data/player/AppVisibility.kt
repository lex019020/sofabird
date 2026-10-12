package dev.vlcremote.app.data.player

import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Whether any Activity of the app is started (visible). Drives polling and the foreground service. */
class AppVisibility : DefaultLifecycleObserver {
    private val _visible = MutableStateFlow(false)
    val visible: StateFlow<Boolean> = _visible.asStateFlow()

    fun install() {
        ProcessLifecycleOwner.get().lifecycle.addObserver(this)
    }

    override fun onStart(owner: LifecycleOwner) {
        _visible.value = true
    }

    override fun onStop(owner: LifecycleOwner) {
        _visible.value = false
    }
}
