(ns hooks.orleans.actors
  "clj-kondo expansions for orleans.actors.

   defactor:

     (defactor ant \"doc\" {:on-error :restart}
       (init [self id] ...)
       (call :step [self state payload] ...))

   becomes a defn of the reference constructor (ant id) plus one fn per
   handler, so the linter sees the bindings and the bodies. Handler arguments
   are positional in the DSL, so they count as used even when a handler does
   not need them.")

(defmacro defactor [name & body]
  (let [[doc body] (if (string? (first body)) [(first body) (rest body)] [nil body])
        [options body] (if (map? (first body)) [(first body) (rest body)] [nil body])
        handlers (for [[kind & clause] body
                       :let [clause (if (= kind 'init) clause (rest clause))          ; drop the message type
                             [spec clause] (if (vector? (first clause)) [nil clause] [(first clause) (rest clause)])
                             [args & handler-body] clause]]
                   `(clojure.core/fn ~args ~spec ~@args ~@handler-body))]                          ; the payload spec counts as used
    `(do
       (defn ~name ~@(when doc [doc]) [~'id] ~'id)
       ~(:state options)                                                               ; so does the state spec
       ~@handlers)))
