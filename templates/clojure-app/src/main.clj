(ns main
  (:require [clojure.data.json :as json]))

(defn -main 
  "Application entry point"
  [& args]
  (println "Hello from Clojure CLR!")
  
  ;; Example of JSON handling
  (let [data {:message "Welcome to Clojure CLR"
              :timestamp (str (System.DateTime/Now))
              :args (vec args)}]
    (println "JSON output:" (json/write-str data)))
  
  ;; Example of .NET interop
  (println "Running on" System.Environment/OSVersion)
  (println ".NET Version:" System.Environment/Version))