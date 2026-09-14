(ns hooks.orleans.actors
  "clj-kondo expansion of orleans.actors/defactor:

     (defactor ant \"doc\" {:on-error :restart}
       (init [self id] ...)
       (call :step [self state payload] ...))

   becomes a defn of the reference constructor (ant id) plus one fn per
   handler, so the linter sees the bindings and the bodies. Handler arguments
   are positional in the DSL, so they count as used even when a handler does
   not need them.")

(defmacro defactor [name & body]
  (let [[doc body] (if (string? (first body)) [(first body) (rest body)] [nil body])
        [_options body] (if (map? (first body)) [(first body) (rest body)] [nil body])
        handlers (for [[kind & clause] body
                       :let [[args & handler-body] (if (= kind 'init) clause (rest clause))]]
                   `(fn ~args ~@args ~@handler-body))]
    `(do
       (defn ~name ~@(when doc [doc]) [~'id] ~'id)
       ~@handlers)))
