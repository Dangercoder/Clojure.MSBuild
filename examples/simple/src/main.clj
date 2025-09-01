(ns main)

(defn -main 
  [& args]
  (println "Hello from Clojure CLR!")
  (when (seq args)
    (println "Arguments:" (vec args))))