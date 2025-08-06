namespace Uno.UI.Dispatching {
	export class NativeDispatcher {
		static _dispatcherCallback: any;

		static _isReady: boolean;

		public static init(isReady : Promise<boolean>) {

			isReady.then(() => {
				NativeDispatcher._dispatcherCallback = (<any>globalThis).DotnetExports.UnoUIDispatching.Uno.UI.Dispatching.NativeDispatcher.DispatcherCallback;

				NativeDispatcher._isReady = true;
				const callback = (timestamp: DOMHighResTimeStamp) => {
					try {
						NativeDispatcher._dispatcherCallback(timestamp);
					} catch (e) {
						console.error(`Unhandled dispatcher exception: ${e} (${e.stack})`);
						throw e;
					}
					requestAnimationFrame(callback);
				};
				requestAnimationFrame(callback);
			});
		}
	}
}
